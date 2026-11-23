// SPDX-License-Identifier: GPL-3.0-or-later
// Windows counterpart of upstream's bundled agent marks: each agent's icon comes from its installed app or CLI
// executable, through Explorer's icon cache (ShellThumbnails). Null when nothing is installed where expected;
// the island then draws a letter mark.
using System.IO;
using System.Windows.Media;
using Notchly.App.Platform.Shell;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

public static class AgentIcons
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>The executable whose icon stands for <paramref name="provider"/>, or null when none is found.</summary>
    public static string? ExecutablePath(AgentProvider provider)
    {
        var home = AgentPaths.Home;
        string[] candidates = provider switch
        {
            AgentProvider.Claude =>
            [
                Path.Combine(LocalAppData, "AnthropicClaude", "claude.exe"), Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(home, ".claude", "local", "claude.exe"),
            ],
            AgentProvider.Codex => [Path.Combine(LocalAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe")],
            AgentProvider.Cursor => [Path.Combine(LocalAppData, "Programs", "cursor", "Cursor.exe")],
            AgentProvider.OpenCode =>
            [
                Path.Combine(RoamingAppData, "npm", "node_modules", "@opencode", "cli", "bin", "opencode.exe"),
                Path.Combine(RoamingAppData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe"),
                Path.Combine(home, ".opencode", "bin", "opencode.exe"),
            ],
            _ => [Path.Combine(RoamingAppData, "npm", "node_modules", "@github", "copilot", "copilot.exe")],
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return AgentPaths.OnPath(provider switch
        {
            AgentProvider.Claude => "claude.exe",
            AgentProvider.Codex => "codex.exe",
            AgentProvider.Cursor => "Cursor.exe",
            AgentProvider.OpenCode => "opencode.exe",
            _ => "copilot.exe",
        });
    }

    /// <summary>A frozen icon about <paramref name="pixels"/> across for <paramref name="provider"/>, or null.</summary>
    public static Task<ImageSource?> GetAsync(AgentProvider provider, int pixels = 32) =>
        ExecutablePath(provider) is { } path ? ShellThumbnails.GetAsync(path, pixels) : Task.FromResult<ImageSource?>(null);
}
