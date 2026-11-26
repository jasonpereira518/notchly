// SPDX-License-Identifier: GPL-3.0-or-later
// Windows-only: what upstream's CommandBarCatalog reads from Spotlight (the apps you can launch). Windows keeps them in the shell's
// Apps folder: every app in Start's "All apps" list, desktop programs and packaged apps alike, each with a display name and a parsing
// name that is its app user model id, which explorer.exe can launch ("shell:AppsFolder\<id>"). Listing them goes through the shell's
// automation object, once a minute at most, on a thread of its own.
using Serilog;

namespace Notchly.App.Platform.Apps;

/// <summary>An app of Start's list: what it is called and the id that launches it.</summary>
public sealed record InstalledApp(string Name, string AppUserModelId)
{
    /// <summary>The path the shell resolves to the app's icon and launches.</summary>
    public string ShellPath => @"shell:AppsFolder\" + AppUserModelId;
}

public static class InstalledApps
{
    private static readonly object Gate = new();
    private static IReadOnlyList<InstalledApp> _cache = [];
    private static DateTime _loadedAt = DateTime.MinValue;

    /// <summary>The apps, from a cache when it is under a minute old. Never throws.</summary>
    public static IReadOnlyList<InstalledApp> List()
    {
        lock (Gate)
        {
            if (_cache.Count > 0 && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(1)) return _cache;
        }
        var fresh = Read();
        lock (Gate)
        {
            if (fresh.Count > 0) { _cache = fresh; _loadedAt = DateTime.UtcNow; }
            return _cache;
        }
    }

    /// <summary>Reads the Apps folder on a thread of its own (the shell wants an apartment).</summary>
    private static IReadOnlyList<InstalledApp> Read()
    {
        IReadOnlyList<InstalledApp> result = [];
        var thread = new Thread(() =>
        {
            try { result = Enumerate(); }
            catch (Exception e) { Log.Warning(e, "The installed apps could not be listed"); }
        }) { IsBackground = true, Name = "Notchly apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(8));
        return result;
    }

    private static IReadOnlyList<InstalledApp> Enumerate()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null || Activator.CreateInstance(type) is not { } shell) return [];
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic items = ((dynamic)shell).NameSpace("shell:AppsFolder").Items();
        int count = items.Count;
        for (var i = 0; i < count; i++)
        {
            try
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                string id = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id) && seen.Add(id)) apps.Add(new InstalledApp(name, id));
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
