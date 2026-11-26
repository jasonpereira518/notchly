// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/QuickTools/QuickTogglesService.swift (quit all) and
// QuitAppsPanelController.swift: every entry point (the panel, the two shortcuts) opens the list rather than quitting at
// once, and quitting is always graceful so unsaved work still asks first. The window itself is Shell/QuitAppsWindow.
using System.Windows.Threading;
using Notchly.App.Platform.Input;
using Notchly.App.Platform.Tools;
using Notchly.App.Shell;
using Notchly.Core.Settings;
using Notchly.Core.Shortcuts;
using Notchly.Core.Tools;

namespace Notchly.App.Tools;

public sealed class QuitAppsService(SettingsStore settings, HotkeyService hotkeys, Dispatcher dispatcher) : IFeatureService
{
    private readonly List<IDisposable> _registrations = [];
    private string? _registered;
    private QuitAppsWindow? _window;

    public bool ShortcutUnavailable { get; private set; }

    public void Sync()
    {
        var all = settings.GetBool(DefaultsKey.QuitAppsShortcutEnabled) ? settings.GetString(DefaultsKey.QuitAppsShortcut) : "";
        var except = settings.GetBool(DefaultsKey.QuitAppsExceptFrontShortcutEnabled) ? settings.GetString(DefaultsKey.QuitAppsExceptFrontShortcut) : "";
        var wanted = all + "|" + except;
        if (wanted == _registered) return;
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        _registered = wanted;
        ShortcutUnavailable = false;
        Register(all, QuitAppsScope.All);
        Register(except, QuitAppsScope.AllExceptFront);
    }

    private void Register(string storage, QuitAppsScope scope)
    {
        if (storage.Length == 0 || Shortcut.Parse(storage) is not { } shortcut) return;
        var handle = hotkeys.Register(shortcut, () => Show(scope));
        if (handle is null) ShortcutUnavailable = true;
        else _registrations.Add(handle);
    }

    /// <summary>The kept apps: the saved list with Explorer always first.</summary>
    public IReadOnlyList<string> Exceptions => QuitAppsSupport.SanitizedExceptions(settings.GetList(DefaultsKey.QuitAppsKeptApps));

    public void AddException(string id)
    {
        var list = Exceptions.Append(id);
        settings.SetList(DefaultsKey.QuitAppsKeptApps, QuitAppsSupport.SanitizedExceptions(list));
    }

    public void RemoveException(string id) =>
        settings.SetList(DefaultsKey.QuitAppsKeptApps, QuitAppsSupport.SanitizedExceptions(Exceptions.Where(e => !string.Equals(e, id, StringComparison.OrdinalIgnoreCase))));

    /// <summary>Opens the list. The scope only decides which batch button Return rests on.</summary>
    public void Show(QuitAppsScope? scope)
    {
        dispatcher.BeginInvoke(() =>
        {
            if (_window is { IsVisible: true } open) { open.Activate(); return; }
            _window = new QuitAppsWindow(this, scope);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
            _window.Activate();
        });
    }

    public IReadOnlyList<OpenApp> Apps() => OpenApps.List().Where(a => QuitAppsSupport.ListsApp(a.Candidate)).ToList();

    /// <summary>One app's own quit works even for a kept app, because picking its row is a clear request.</summary>
    public bool Quit(OpenApp app) => QuitAppsSupport.CanQuitRow(app.Id) && OpenApps.Quit(app);

    /// <summary>A batch: every app the rules allow, leaving out the front one when asked. Returns the apps asked to quit.</summary>
    public IReadOnlyList<OpenApp> QuitBatch(IReadOnlyList<OpenApp> apps, bool exceptFront)
    {
        var front = exceptFront ? apps.FirstOrDefault(a => a.IsFront)?.Key : null;
        var keys = QuitAppsSupport.Targets(apps.Select(a => a.Candidate), Exceptions, front).ToHashSet();
        var asked = apps.Where(a => keys.Contains(a.Key)).ToList();
        foreach (var app in asked) OpenApps.Quit(app);
        return asked;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        _window?.Close();
    }
}
