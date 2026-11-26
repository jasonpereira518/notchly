// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Services/CommandBar/CommandBarService.swift: the one field that finds and runs what Notchly does. The shortcut opens
// the palette over whatever you are doing; the service gathers the rows from the sources, ranks them with the Core engine, learns from
// what you run, and runs the chosen one. Nothing you type is saved: only which rows you ran, and a digest of the letters that found them.
using System.Globalization;
using System.Security.Cryptography;
using System.Windows.Threading;
using Notchly.App.Platform;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Windowing;
using Notchly.App.Shell;
using Notchly.App.Tools;
using Notchly.Core.CommandBar;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.CommandBar;

public enum CommandBarMode { Search, Confirm, Argument, Actions, Alias }

internal sealed class CommandBarService : IFeatureService
{
    private readonly App app;
    private readonly Dispatcher _dispatcher;
    private readonly CommandBarRows _rows;
    private IDisposable? _registration;
    private string? _registered;
    private CommandBarWindow? _window;
    private readonly CommandBarLearningContext _learning = new();
    private IReadOnlyList<CommandBarEntry> _quick = [], _apps = [], _windows = [], _quit = [], _answers = [], _selection = [], _menus = [];
    private CancellationTokenSource? _loading;
    private string _query = "";
    private int _step;

    public CommandBarService(App app)
    {
        this.app = app;
        _dispatcher = app.Dispatcher;
        _rows = new CommandBarRows(this);
    }

    public App App => app;
    public bool ShortcutUnavailable { get; private set; }
    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>The window that was in front when the bar opened, which rows that act on "the app you were in" go back to.</summary>
    public IntPtr PreviousWindow { get; private set; }

    public IReadOnlyList<CommandBarEntry> Rows { get; private set; } = [];
    public IReadOnlyDictionary<int, string> Titles { get; private set; } = new Dictionary<int, string>();
    public int SelectedIndex { get; private set; }
    public CommandBarMode Mode { get; private set; }
    public string Prompt { get; private set; } = "";
    public string SelectionPreview { get; private set; } = "";

    /// <summary>What was selected in the app the bar was opened over, when it could be read.</summary>
    public string? SelectedText { get; private set; }

    /// <summary>Raised on the UI thread when the rows, the selection or the mode changed.</summary>
    public event Action? Changed;

    public string Query => _query;
    public SettingsStore Settings => app.Settings;

    // MARK: Opening

    public void Sync()
    {
        var wanted = Settings.GetBool(DefaultsKey.CommandBarShortcutEnabled) ? Settings.GetString(DefaultsKey.CommandBarShortcut) : "";
        if (wanted == _registered) return;
        _registration?.Dispose();
        _registration = null;
        _registered = wanted;
        ShortcutUnavailable = false;
        if (wanted.Length == 0 || Shortcut.Parse(wanted) is not { } shortcut) return;
        _registration = app.Hotkeys.Register(shortcut, () => _dispatcher.BeginInvoke(Toggle));
        ShortcutUnavailable = _registration is null;
    }

    public void Toggle()
    {
        if (IsOpen) { Close(); return; }
        Open();
    }

    public void Open()
    {
        var front = WindowInventory.Foreground();
        PreviousWindow = front != IntPtr.Zero && ForegroundApps.Describe(front)?.ProcessId != (uint)Environment.ProcessId ? front : IntPtr.Zero;
        _query = "";
        Mode = CommandBarMode.Search;
        Prompt = "";
        SelectedIndex = 0;
        LoadLearning();
        // What can be had at once is built now; apps, windows and the rest follow a moment later and refresh the list.
        _quick = [.. _rows.Actions(), .. _rows.SettingsPages(), .. _rows.WindowsSettings(), .. _rows.Folders(), .. _rows.Links()];
        _apps = _windows = _quit = _answers = _selection = _menus = [];
        SelectionPreview = "";
        SelectedText = null;
        _window = new CommandBarWindow(this);
        _window.Closed += (_, _) => { _window = null; _loading?.Cancel(); SaveLearning(); };
        Recompute();
        _window.Open();
        _ = LoadSlowSources();
    }

    public void Close() => _window?.Close();

    /// <summary>Apps, windows, answers and what is selected: read off the UI thread, then shown.</summary>
    private async Task LoadSlowSources()
    {
        _loading?.Cancel();
        var source = _loading = new CancellationTokenSource();
        var token = source.Token;
        var previous = PreviousWindow;
        var snapshot = await Task.Run(() => (Apps: _rows.Apps(), Windows: _rows.Windows(), Quit: _rows.QuitApps(), Answers: _rows.Answers(),
                                             Selection: previous == IntPtr.Zero ? null : Platform.Apps.SelectedTextReader.Read(),
                                             Menus: previous != IntPtr.Zero && ForegroundApps.Describe(previous) is { } described ? _rows.MenuRows(previous, described) : []));
        if (token.IsCancellationRequested || _window is null) return;
        _apps = snapshot.Apps;
        _windows = snapshot.Windows;
        _quit = snapshot.Quit;
        _answers = snapshot.Answers;
        _menus = snapshot.Menus;
        SelectedText = snapshot.Selection;
        _selection = _rows.Selection(snapshot.Selection, out var preview);
        SelectionPreview = preview;
        Recompute();
    }

    // MARK: Typing

    public void SetQuery(string text)
    {
        if (Mode == CommandBarMode.Alias) { _aliasDraft = text; return; }
        if (text == _query) return;
        _query = text;
        if (Mode != CommandBarMode.Search) { Mode = CommandBarMode.Search; Prompt = ""; _actionsTarget = null; }
        SelectedIndex = 0;
        Recompute();
    }

    private CommandBarPreferenceSnapshot ReadPreferences() => new(
        CommandBarPreferences.DisabledSources(Settings.GetString(DefaultsKey.CommandBarDisabledSources)),
        CommandBarPreferences.DecodeAliases(Settings.GetString(DefaultsKey.CommandBarAliases)),
        CommandBarPreferences.DecodePins(Settings.GetString(DefaultsKey.CommandBarPins)),
        CommandBarPreferences.DecodeHidden(Settings.GetString(DefaultsKey.CommandBarHidden)));

    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Recompute()
    {
        if (Mode is CommandBarMode.Actions or CommandBarMode.Alias) return;
        var preferences = ReadPreferences();
        var trimmed = _query.Trim();
        var culture = CultureInfo.CurrentCulture;
        if (trimmed.Length == 0 && Settings.GetBool(DefaultsKey.CommandBarCompactMode))
        {
            Rows = [];
            Titles = new Dictionary<int, string>();
        }
        else if (trimmed.Length == 0)
        {
            var offerable = _quick.Concat(_apps).Concat(_windows).ToList();
            var browse = CommandBarEngine.Browse(_selection, SelectionPreview, offerable, _quick, _rows.ClipboardBrowse(6), preferences, _learning, Now);
            Rows = browse.Rows;
            Titles = browse.Titles;
        }
        else if (CommandBarSearch.EmojiQuery(trimmed) is { } emoji)
        {
            Titles = new Dictionary<int, string>();
            Rows = preferences.IsEnabled(CommandBarSource.Emoji) ? CommandBarEngine.SearchEmoji(emoji, _rows.Emoji(), _learning, Now) : [];
        }
        else
        {
            Titles = new Dictionary<int, string>();
            var answer = preferences.IsEnabled(CommandBarSource.Calculator) ? _rows.Answer(trimmed, culture) : null;
            var openUrl = _rows.OpenUrl(trimmed);
            var pool = new List<CommandBarEntry>(_selection);
            pool.AddRange(_quick);
            pool.AddRange(_apps);
            pool.AddRange(_windows);
            pool.AddRange(_answers);
            if (CommandBarSearch.MatchesVerb(trimmed, CommandBarStrings.QuitFormat)) pool.AddRange(_quit);
            if (trimmed.Length >= 2) pool.AddRange(_menus);
            pool.AddRange(_rows.ClipboardMatching(trimmed));
            pool.AddRange(_rows.FilesFor(trimmed, Refresh));
            Rows = CommandBarEngine.Search(trimmed, pool, answer, openUrl, preferences, _learning, Now,
                text => pool.Any(e => e.NumericRange is not null && CommandBarSearch.Matches(e.Title, text, e.Keywords)));
        }
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Rows.Count - 1));
        Changed?.Invoke();
    }

    /// <summary>A source that answered late (files, the clipboard) asks for the list to be worked out again.</summary>
    public void Refresh() => _dispatcher.BeginInvoke(() => { if (IsOpen && Mode == CommandBarMode.Search) Recompute(); });

    // MARK: Selection and running

    public CommandBarEntry? Selected => Rows.Count > 0 && SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;

    public void Select(int index)
    {
        if (index < 0 || index >= Rows.Count || index == SelectedIndex) return;
        SelectedIndex = index;
        Changed?.Invoke();
    }

    /// <summary>Moving past either end comes back around.</summary>
    public void Move(int delta)
    {
        if (Rows.Count == 0) return;
        Select(((SelectedIndex + delta) % Rows.Count + Rows.Count) % Rows.Count);
    }

    /// <summary>Esc: back out of a confirmation or number prompt first, then clear the field, then close.</summary>
    public bool StepBack()
    {
        if (Mode == CommandBarMode.Alias) { Mode = CommandBarMode.Actions; Prompt = ""; _aliasDraft = ""; Changed?.Invoke(); return true; }
        if (Mode == CommandBarMode.Actions) { LeaveActions(); return true; }
        if (Mode != CommandBarMode.Search) { Mode = CommandBarMode.Search; Prompt = ""; Changed?.Invoke(); return true; }
        return false;
    }

    /// <summary>Return: runs the row, after the confirmation or the number it asks for.</summary>
    public void RunSelected(bool alternate = false)
    {
        if (Mode == CommandBarMode.Alias) { CommitAlias(); return; }
        if (Selected is not { } entry) return;
        if (Mode == CommandBarMode.Actions) { entry.Run(null); return; }
        if (Mode == CommandBarMode.Confirm) { Execute(entry, null); return; }
        int? number = null;
        if (entry.NumericRange is { } range)
        {
            var split = CommandBarSearch.SplitTrailingNumber(_query.Trim());
            number = split.Number is { } n ? Math.Clamp(n, range.Min, range.Max) : null;
            if (number is null && !entry.NumericIsOptional)
            {
                Mode = CommandBarMode.Argument;
                Prompt = CommandBarStrings.Format("{0} to {1}", range.Min, range.Max) + " · " + CommandBarStrings.ArgumentHint;
                Changed?.Invoke();
                return;
            }
        }
        if (entry.ConfirmationPrompt is { } prompt)
        {
            Mode = CommandBarMode.Confirm;
            Prompt = prompt + " · " + CommandBarStrings.ConfirmHint;
            Changed?.Invoke();
            return;
        }
        Execute(entry, number);
    }

    private void Execute(CommandBarEntry entry, int? number)
    {
        Record(entry);
        if (!entry.KeepsBarOpen) Close();
        // After the palette is gone, so a row that acts on the app you came from finds it in front again.
        _dispatcher.BeginInvoke(() =>
        {
            try { entry.Run(number); }
            catch (Exception e) { Log.Warning(e, "A command bar row failed: {Id}", entry.Id); }
        }, DispatcherPriority.Background);
    }

    /// <summary>Puts text into the field and searches it (a row that feeds the bar instead of acting).</summary>
    public void PutInSearch(string text) => _dispatcher.BeginInvoke(() => _window?.SetQueryText(text));

    /// <summary>
    /// Types text at the caret of the app the bar was opened over (over its selection, if any). It waits for the keys the person still
    /// holds to come up, and does nothing in a password field or when the bar was opened over Notchly itself.
    /// </summary>
    public async Task TypeAtCaretAsync(string text)
    {
        if (PreviousWindow == IntPtr.Zero || text.Length == 0) return;
        await RestoreFocusAsync();
        for (var i = 0; i < 100 && KeyState.HeldModifiers().Count > 0; i++) await Task.Delay(15);
        await Task.Delay(60);
        if (app.Passwords.InPasswordField) { QuickToolHud.Show("Not typed into a password field"); return; }
        app.Injector.Text(text, Notchly.Core.Input.InputSource.CommandBar);
    }

    /// <summary>Pastes a copied entry into the window the bar was opened over.</summary>
    public async Task PasteAsync(Notchly.App.Pasteboard.ClipboardHistoryService history, Notchly.Core.Clipboard.ClipboardEntry entry)
    {
        var paster = new Notchly.App.Pasteboard.ClipboardPaster(app.Injector);
        await paster.PasteAsync(history, [entry], PreviousWindow);
    }

    /// <summary>Brings the window the bar was opened over back to the front and waits for it to take the keys.</summary>
    public async Task RestoreFocusAsync()
    {
        if (PreviousWindow == IntPtr.Zero) return;
        WindowInventory.Activate(PreviousWindow);
        await Task.Delay(140);
    }

    // MARK: Actions on a row (Ctrl+K)

    private CommandBarEntry? _actionsTarget;
    private IReadOnlyList<CommandBarEntry> _savedRows = [];
    private IReadOnlyDictionary<int, string> _savedTitles = new Dictionary<int, string>();
    private int _savedSelected;
    private string _aliasDraft = "";

    /// <summary>The text the field shows while a name is being typed.</summary>
    public string AliasDraft => _aliasDraft;

    public void OpenActions()
    {
        if (Mode != CommandBarMode.Search || Selected is not { } target || target.IsAnswer) return;
        var rows = new List<CommandBarEntry>();
        CommandBarEntry Item(string id, string title, string glyph, Action run) =>
            new("rowaction." + id, "rowaction." + id, title, "", CommandBarIcon.Glyph(glyph), _ => run()) { CountsUsage = false };
        var aliases = ReadPreferences().Aliases;
        if (CommandBarPreferences.AcceptsPin(target.Id))
        {
            var pinned = CommandBarPreferences.DecodePins(Settings.GetString(DefaultsKey.CommandBarPins)).Contains(target.StableKey);
            rows.Add(Item("pin", pinned ? CommandBarStrings.ActionUnpin : CommandBarStrings.ActionPin, "\uE718", () => TogglePin(target)));
        }
        if (CommandBarPreferences.AcceptsAlias(target.Id))
            rows.Add(Item("name", aliases.ContainsKey(target.StableKey) ? CommandBarStrings.ActionRename : CommandBarStrings.ActionName, "\uE8AC", () => BeginAlias(target)));
        if (target.CanReveal) rows.Add(Item("reveal", CommandBarStrings.ActionReveal, "\uE8B7", () => { Close(); Platform.Shell.ShellActions.Reveal(target.RevealPath!); }));
        if (target.CountsUsage) rows.Add(Item("forget", CommandBarStrings.ActionForget, "\uE72C", () => Forget(target)));
        rows.Add(Item("hide", CommandBarStrings.ActionHide, "\uED1A", () => Hide(target)));
        _actionsTarget = target;
        _savedRows = Rows;
        _savedTitles = Titles;
        _savedSelected = SelectedIndex;
        Rows = rows;
        Titles = new Dictionary<int, string> { [0] = CommandBarStrings.ActionsTitle + " · " + target.Title };
        SelectedIndex = 0;
        Mode = CommandBarMode.Actions;
        Prompt = "";
        Changed?.Invoke();
    }

    private void LeaveActions()
    {
        Mode = CommandBarMode.Search;
        _actionsTarget = null;
        Rows = _savedRows;
        Titles = _savedTitles;
        SelectedIndex = _savedSelected;
        Changed?.Invoke();
    }

    private void TogglePin(CommandBarEntry target)
    {
        Settings.Set(DefaultsKey.CommandBarPins, CommandBarPreferences.EncodePins(CommandBarPreferences.TogglingPin(target.StableKey, CommandBarPreferences.DecodePins(Settings.GetString(DefaultsKey.CommandBarPins)))));
        Mode = CommandBarMode.Search;
        Recompute();
    }

    private void Hide(CommandBarEntry target)
    {
        Settings.Set(DefaultsKey.CommandBarHidden, CommandBarPreferences.EncodeHidden(CommandBarPreferences.TogglingHidden(target.StableKey, CommandBarPreferences.DecodeHidden(Settings.GetString(DefaultsKey.CommandBarHidden)))));
        Mode = CommandBarMode.Search;
        Recompute();
    }

    private void Forget(CommandBarEntry target)
    {
        _usage = _usage.Where(p => p.Key != target.Id).ToDictionary(p => p.Key, p => p.Value);
        _learning.Usage = _usage;
        _learning.Habits.Remove(target.Id);
        _learning.Memory.Forget(target.Id);
        SaveLearning();
        Mode = CommandBarMode.Search;
        Recompute();
    }

    private void BeginAlias(CommandBarEntry target)
    {
        _aliasDraft = ReadPreferences().Aliases.GetValueOrDefault(target.StableKey, "");
        Mode = CommandBarMode.Alias;
        Prompt = CommandBarStrings.AliasPlaceholder + " · " + CommandBarStrings.ArgumentHint;
        Changed?.Invoke();
    }

    private void CommitAlias()
    {
        if (_actionsTarget is not { } target) return;
        var aliases = ReadPreferences().Aliases;
        if (_aliasDraft.Trim().Length > 0 && CommandBarPreferences.RowUsingAlias(_aliasDraft, aliases, target.StableKey) is not null)
        {
            Prompt = CommandBarStrings.Format(CommandBarStrings.AliasTakenFormat, _aliasDraft.Trim());
            Changed?.Invoke();
            return;
        }
        Settings.Set(DefaultsKey.CommandBarAliases, CommandBarPreferences.EncodeAliases(CommandBarPreferences.SettingAlias(_aliasDraft, target.StableKey, aliases)));
        _aliasDraft = "";
        Mode = CommandBarMode.Search;
        Prompt = "";
        _actionsTarget = null;
        Recompute();
    }

    // MARK: Learning

    private void LoadLearning()
    {
        var resolved = CommandBarLearning.ResolveInstallationKey(ReadProtectedKey());
        var usage = CommandBarUsage.Decode(Settings.GetString(DefaultsKey.CommandBarUsage));
        var habits = _learning.Habits;
        habits.Reload(resolved.WasReplaced ? "{}" : Settings.GetString(DefaultsKey.CommandBarQueryHabits));
        if (resolved.WasReplaced) WriteProtectedKey(resolved.StorageValue);
        _usage = usage;
        _habitKey = resolved.Key;
        _learning.Usage = _usage;
        _learning.HabitKey = _habitKey;
    }

    private Dictionary<string, CommandBarUse> _usage = [];
    private byte[]? _habitKey;

    private string? ReadProtectedKey()
    {
        var stored = Settings.GetString(DefaultsKey.CommandBarQueryHabitKey);
        if (stored.Length == 0) return null;
        try { return Convert.ToBase64String(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    private void WriteProtectedKey(string base64Key)
    {
        var protectedBytes = ProtectedData.Protect(Convert.FromBase64String(base64Key), null, DataProtectionScope.CurrentUser);
        Settings.Set(DefaultsKey.CommandBarQueryHabitKey, Convert.ToBase64String(protectedBytes));
    }

    /// <summary>What was run counts toward suggestions, toward the letters that found it, and toward this session's memory.</summary>
    private void Record(CommandBarEntry entry)
    {
        if (!entry.CountsUsage) return;
        var now = Now;
        _usage = CommandBarUsage.Recording(_usage, entry.Id, now);
        _learning.Usage = _usage;
        var trimmed = _query.Trim();
        if (trimmed.Length > 0)
        {
            _learning.Habits.Record(CommandBarQueryHabits.Prepare(trimmed, _habitKey, _learning.Preparation), entry.Id, now);
            _learning.Memory.Record(trimmed, entry.Id, ++_step);
        }
        SaveLearning();
    }

    private void SaveLearning()
    {
        Settings.Set(DefaultsKey.CommandBarUsage, CommandBarUsage.Encode(_usage));
        Settings.Set(DefaultsKey.CommandBarQueryHabits, CommandBarQueryHabits.Encode(_learning.Habits.Store));
    }

    public void ForgetLearning()
    {
        _usage = [];
        _learning.Habits.ForgetAll();
        _learning.Memory.Clear();
        SaveLearning();
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _loading?.Cancel();
        _window?.Close();
    }
}
