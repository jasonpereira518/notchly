// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/ScratchpadService.swift: the notes (up to twelve named pads, kept in
// %APPDATA%\Notchly\Scratchpad.json), the shortcut that opens the floating pad, saving 0.8 s after the last edit, and the retention
// rule that clears idle text when the pad opens. A file that cannot be read is never overwritten: the pad says so and saves nothing.
using System.Windows.Threading;
using Notchly.App.Platform.Input;
using Notchly.App.Shell;
using Notchly.Core;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Tools;
using Serilog;

namespace Notchly.App.Tools;

public sealed class ScratchpadService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher) : IFeatureService
{
    public const string DefaultName = "Scratchpad";
    public const string LoadFailed = "Your notes could not be opened. They were left unchanged.";
    public const string SaveFailed = "Your notes could not be saved. Copy them elsewhere before quitting.";

    private readonly DispatcherTimer _save = new(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(0.8) };
    private IDisposable? _registration;
    private string? _registered;
    private ScratchpadStore? _store;
    private ScratchpadPadWindow? _window;
    private bool _dirty;

    public bool ShortcutUnavailable { get; private set; }

    /// <summary>The notes, null until they were read.</summary>
    public ScratchpadDocument? Document { get; private set; }

    /// <summary>Set when the notes could not be read: nothing is saved over them.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Set when the last save failed.</summary>
    public bool SaveFailedLast { get; private set; }

    public event Action? SaveStateChanged;

    public bool IsOpen => _window is { IsVisible: true };

    public void Sync()
    {
        var wanted = settings.GetBool(DefaultsKey.ScratchpadShortcutEnabled) ? settings.GetString(DefaultsKey.ScratchpadShortcut) : "";
        if (wanted != _registered)
        {
            _registration?.Dispose();
            _registration = null;
            _registered = wanted;
            ShortcutUnavailable = false;
            if (wanted.Length > 0 && Shortcut.Parse(wanted) is { } shortcut)
            {
                _registration = hotkeys.Register(shortcut, () => dispatcher.BeginInvoke(Toggle));
                ShortcutUnavailable = _registration is null;
            }
        }
        _save.Tick -= OnSaveTick;
        _save.Tick += OnSaveTick;
    }

    public ScratchpadRetention Retention => ScratchpadRetentions.Sanitized(settings.GetString(DefaultsKey.ScratchpadRetention));

    /// <summary>Opens the pad, or closes it when it is open. The notes are read (and idle ones cleared) as it opens.</summary>
    public void Toggle()
    {
        if (_window is { IsVisible: true } open) { open.Close(); return; }
        Load();
        _window = new ScratchpadPadWindow(this, settings);
        _window.Closed += (_, _) => { _window = null; Flush(); };
        _window.Show();
        _window.Activate();
    }

    private void Load()
    {
        _store ??= new ScratchpadStore(AppIdentity.DataDirectory);
        try
        {
            Document = _store.Load(DefaultName, Retention, DateTimeOffset.Now);
            LoadError = null;
        }
        catch (Exception e)
        {
            Log.Warning(e, "The scratchpad could not be read");
            LoadError = LoadFailed;
            Document ??= ScratchpadDocument.Initial(DefaultName);
        }
    }

    /// <summary>Replaces the notes and saves them shortly.</summary>
    public void Update(ScratchpadDocument document)
    {
        Document = document;
        _dirty = true;
        _save.Stop();
        _save.Start();
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _save.Stop();
        Flush();
    }

    /// <summary>Writes the notes now, when they changed.</summary>
    public void Flush()
    {
        _save.Stop();
        if (!_dirty || _store is null || Document is null) return;
        _dirty = false;
        var ok = _store.Save(Document);
        if (SaveFailedLast == ok) { SaveFailedLast = !ok; SaveStateChanged?.Invoke(); }
        if (!ok) Log.Warning("The scratchpad could not be saved");
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _save.Tick -= OnSaveTick;
        _window?.Close();
        Flush();
    }
}
