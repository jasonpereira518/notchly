// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream AgentClaudeServer.Request and its executable lookup (Services/AgentUsage/AgentClaudeServer.swift,
// AgentCLIConversation.swift) and the cadence in AgentUsageService.refreshClaudeLimits: asks Claude Code for the
// account's /usage data over its stdin control protocol. On Windows the CLI is claude.exe (or npm's claude.cmd),
// started hidden in the user profile and killed with its children after the timeout. No prompt, tool, MCP server
// or hook runs, and Claude's .credentials.json is never read: only .claude.json's account metadata.
using System.Diagnostics;
using System.IO;
using System.Text;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

/// <summary>One account-wide limits check of Claude Code, and when the next one is due. Thread-safe.</summary>
public sealed class ClaudeLimitsReader
{
    /// <summary>What a check found. <paramref name="Account"/> is <c>.claude.json</c>'s identity and plan, read after the
    /// check (in memory only).</summary>
    public sealed record Result(AgentLimits? Limits, AgentClaudeServer.Failure? Failure, ClaudeAccountReading Account);

    private readonly Lock gate = new();
    private DateTimeOffset attempted = AgentTime.DistantPast;
    private int unreachable;
    private CancellationTokenSource? running;

    /// <summary>The last check's failure; null after a success or before any check.</summary>
    public AgentClaudeServer.Failure? LastFailure { get; private set; }
    public bool Checking { get { lock (gate) return running is not null; } }

    /// <summary>Whether a check is due: a minute after the last one while the page shows, five otherwise, and longer after
    /// failures. The caller asks only while limits are in use (<see cref="NotchAgentSupport.LimitsInUse"/>) and online.</summary>
    public bool IsDue(DateTimeOffset now, bool pageVisible)
    {
        lock (gate)
            return running is null && now.Since(attempted) >= AgentClaudeServer.Interval(LastFailure, unreachable, pageVisible);
    }

    /// <summary>A deliberate retry, as after signing in: the next <see cref="IsDue"/> is true.</summary>
    public void Retry()
    {
        lock (gate) attempted = AgentTime.DistantPast;
    }

    /// <summary>Back online, the checks start over from the shortest wait.</summary>
    public void ResetBackoff()
    {
        lock (gate) unreachable = 0;
    }

    /// <summary>Ends a check in flight; the next one is due at once.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            running?.Cancel();
            attempted = AgentTime.DistantPast;
        }
    }

    /// <summary>The Claude Code CLI: the native installer's, the old local install's, one on PATH, or npm's shim.</summary>
    public static string? FindExecutable()
    {
        var home = AgentPaths.Home;
        foreach (var candidate in new[] { Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(home, ".claude", "local", "claude.exe") })
            if (File.Exists(candidate)) return candidate;
        if (AgentPaths.OnPath("claude.exe", "claude.cmd") is { } found) return found;
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        return File.Exists(npm) ? npm : null;
    }

    /// <summary>What <c>.claude.json</c> says about the signed-in account (identity and plan only).</summary>
    public static ClaudeAccountReading ReadAccount()
    {
        try
        {
            var info = new FileInfo(AgentPaths.ClaudeProfileFile);
            if (!info.Exists || info.Length > 4 << 20) return ClaudeAccountReading.None;
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return ClaudeAccountReading.Parse(bytes, AgentPricing.List);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ClaudeAccountReading.None; }
    }

    /// <summary>Runs one check. Concurrent calls while one runs fail as unreachable without starting Claude Code.</summary>
    public async Task<Result> CheckAsync(CancellationToken token = default)
    {
        CancellationTokenSource mine;
        lock (gate)
        {
            if (running is not null) return new Result(null, AgentClaudeServer.Failure.Unreachable, ClaudeAccountReading.None);
            mine = CancellationTokenSource.CreateLinkedTokenSource(token);
            running = mine;
            attempted = DateTimeOffset.UtcNow;
        }
        Result result;
        try { result = await RunAsync(mine.Token); }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = new Result(null, AgentClaudeServer.Failure.Unreachable, ClaudeAccountReading.None);
        }
        lock (gate)
        {
            running = null;
            LastFailure = result.Failure;
            unreachable = result.Failure == AgentClaudeServer.Failure.Unreachable ? unreachable + 1 : 0;
        }
        mine.Dispose();
        return result;
    }

    private static async Task<Result> RunAsync(CancellationToken token)
    {
        if (FindExecutable() is not { } executable) return new Result(null, AgentClaudeServer.Failure.Missing, ClaudeAccountReading.None);
        var started = DateTimeOffset.UtcNow;
        var before = ReadAccount();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = AgentPaths.Home,
        };
        foreach (var argument in AgentClaudeServer.Arguments) info.ArgumentList.Add(argument);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) current[key] = value;
        string? settings = null;
        try
        {
            var file = new FileInfo(AgentPaths.ClaudeSettingsFile);
            if (file.Exists && file.Length < 1 << 20) settings = await File.ReadAllTextAsync(file.FullName, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        info.Environment.Clear();
        foreach (var (key, value) in AgentClaudeServer.Environment(current, settings, AgentPaths.ClaudeConfigOverride)) info.Environment[key] = value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(AgentClaudeServer.Timeout));
        using var process = new Process { StartInfo = info };
        if (!process.Start()) return new Result(null, AgentClaudeServer.Failure.Unreachable, ClaudeAccountReading.None);
        // What Claude Code writes on stderr is never kept.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        try
        {
            var (_, initialized) = await AskAsync(process, AgentClaudeServer.InitializeRequest, AgentClaudeServer.InitializeId, timeout.Token);
            if (initialized is { } failure) return new Result(null, failure, ClaudeAccountReading.None);
            var (usage, failed) = await AskAsync(process, AgentClaudeServer.UsageRequest, AgentClaudeServer.UsageId, timeout.Token);
            if (failed is { } other) return new Result(null, other, ClaudeAccountReading.None);
            if (usage is null || !AgentClaudeServer.Available(usage)) return new Result(null, AgentClaudeServer.Failure.NeedsSignIn, ClaudeAccountReading.None);
            // The saved copy is read once Claude Code has exited, so what it fetched for this answer is on disk.
            process.StandardInput.Close();
            using (var exit = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                try { await process.WaitForExitAsync(exit.Token); }
                catch (OperationCanceledException) { }
            }
            token.ThrowIfCancellationRequested();
            var after = ReadAccount();
            if (before.Identity != after.Identity) return new Result(null, AgentClaudeServer.Failure.Unreachable, after);
            // A copy saved before this check began was answered in place of the account, which was not reached.
            if (AgentClaudeServer.Observed(started, after.UsageFetched, DateTimeOffset.UtcNow) is not { } observed
                || AgentClaudeServer.Limits(usage, observed) is not { } reading)
                return new Result(null, AgentClaudeServer.Failure.Unreachable, after);
            return new Result(reading, null, after);
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    /// <summary>Writes one control request and reads stdout until its response.</summary>
    private static async Task<(string? Result, AgentClaudeServer.Failure? Failure)> AskAsync(Process process, string request, string id,
                                                                                              CancellationToken token)
    {
        await process.StandardInput.WriteAsync(request.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            if (line.Length > 8 << 20) continue;
            if (AgentClaudeServer.Response(line, id) is { } response) return response;
        }
        return (null, AgentClaudeServer.Failure.Unreachable);
    }
}
