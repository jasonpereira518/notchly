// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream AgentLogRoot (Services/AgentUsage/AgentUsageStore.swift) and the folder half of
// ClaudeAccountProfile.swift: where each agent keeps its logs on Windows. The agents honour CLAUDE_CONFIG_DIR,
// CODEX_HOME and XDG_DATA_HOME as on other systems; a root kept as a link elsewhere is resolved, so file events
// and discovery spell paths the same way.
using System.IO;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

/// <summary>A folder an agent keeps its session logs in.</summary>
public sealed record AgentLogRoot(AgentProvider Provider, string Path)
{
    public bool Exists => Directory.Exists(Path);

    /// <summary>Discovery and live file events admit the same provider-specific logs.</summary>
    public bool Accepts(string path)
    {
        var prefix = Path + System.IO.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (Provider == AgentProvider.OpenCode)
            return string.Equals(path, System.IO.Path.Combine(Path, OpenCodeRows.Database), StringComparison.OrdinalIgnoreCase);
        if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (Provider != AgentProvider.Copilot) return true;
        var parts = path[prefix.Length..].Split(System.IO.Path.DirectorySeparatorChar);
        return parts.Length == 2 && parts[0].Length > 0 && !parts[0].StartsWith('.')
               && string.Equals(parts[1], "events.jsonl", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Where the agents keep their logs, records and settings on this computer.</summary>
public static class AgentPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>The folder Claude Code was told to use instead of its own, or null.</summary>
    public static string? ClaudeConfigOverride => Variable("CLAUDE_CONFIG_DIR");

    /// <summary>Claude Code's configuration folders: the override alone, or <c>.claude</c> and <c>.config\claude</c>.</summary>
    public static IReadOnlyList<string> ClaudeConfigDirectories =>
        ClaudeConfigOverride is { } dir ? [dir] : [System.IO.Path.Combine(Home, ".claude"), System.IO.Path.Combine(Home, ".config", "claude")];

    /// <summary>The folder Claude Code keeps its user settings in.</summary>
    public static string ClaudeConfigDirectory => ClaudeConfigOverride ?? System.IO.Path.Combine(Home, ".claude");

    /// <summary><c>.claude.json</c>: beside the profile folder by default, inside it when overridden. Only its account
    /// metadata is read; <c>.credentials.json</c> never is.</summary>
    public static string ClaudeProfileFile =>
        System.IO.Path.Combine(ClaudeConfigOverride ?? Home, ".claude.json");

    public static string ClaudeSettingsFile => System.IO.Path.Combine(ClaudeConfigDirectory, "settings.json");

    public static string CodexHome => Variable("CODEX_HOME") ?? System.IO.Path.Combine(Home, ".codex");

    public static string OpenCodeDataDirectory =>
        System.IO.Path.Combine(Variable("XDG_DATA_HOME") ?? System.IO.Path.Combine(Home, ".local", "share"), "opencode");

    public static string CopilotSessions => System.IO.Path.Combine(Home, ".copilot", "session-state");

    /// <summary>Every root of <paramref name="providers"/>, resolved, existing or not, in the island's order.</summary>
    public static IReadOnlyList<AgentLogRoot> Roots(IReadOnlySet<AgentProvider> providers)
    {
        var roots = new List<AgentLogRoot>();
        if (providers.Contains(AgentProvider.Claude))
            roots.AddRange(ClaudeConfigDirectories.Select(d => new AgentLogRoot(AgentProvider.Claude, Canonical(System.IO.Path.Combine(d, "projects")))));
        if (providers.Contains(AgentProvider.Codex))
        {
            roots.Add(new AgentLogRoot(AgentProvider.Codex, Canonical(System.IO.Path.Combine(CodexHome, "sessions"))));
            roots.Add(new AgentLogRoot(AgentProvider.Codex, Canonical(System.IO.Path.Combine(CodexHome, "archived_sessions"))));
        }
        if (providers.Contains(AgentProvider.OpenCode)) roots.Add(new AgentLogRoot(AgentProvider.OpenCode, Canonical(OpenCodeDataDirectory)));
        if (providers.Contains(AgentProvider.Copilot)) roots.Add(new AgentLogRoot(AgentProvider.Copilot, Canonical(CopilotSessions)));
        return roots.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The <c>sessions</c> folders Claude Code keeps its process records in, beside each log root.</summary>
    public static IReadOnlyList<string> ClaudeSessionFolders(IEnumerable<AgentLogRoot> roots) =>
        roots.Where(r => r.Provider == AgentProvider.Claude)
            .Select(r => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(r.Path) ?? r.Path, "sessions")).ToList();

    /// <summary>The full path with a linked folder resolved to its target; the path as given while nothing is there.</summary>
    public static string Canonical(string path)
    {
        string full;
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        try
        {
            var info = new DirectoryInfo(full);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
                return System.IO.Path.TrimEndingDirectorySeparator(target.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return full;
    }

    /// <summary>The first of <paramref name="names"/> found in a folder on PATH.</summary>
    public static string? OnPath(params string[] names)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            var trimmed = folder.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            foreach (var name in names)
            {
                try
                {
                    var candidate = System.IO.Path.Combine(trimmed, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }
}
