// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream AgentLogCursor and AgentLogReader (Services/AgentUsage/AgentUsageStore.swift): how far into
// one log reading has got, the fingerprint that tells a log rewritten in place, discovery and incremental reads.
// Windows names a file by its volume serial and file index (FileIdentity) instead of an inode, and every handle
// shares read, write and delete, so an agent writing, rotating or deleting its log is never in the way.
using System.IO;
using Microsoft.Win32.SafeHandles;
using Notchly.App.Platform.Shell;
using Notchly.Core.Agents;

namespace Notchly.App.Platform.Agents;

/// <summary>How far reading one OpenCode database has got, beside the last rowid read that the offset holds.</summary>
internal sealed class OpenCodeProgress
{
    /// <summary>Replies still being written, by id, with what they held when read.</summary>
    public Dictionary<string, (long Updated, long Length, DateTimeOffset Changed)> Open { get; } = [];
    /// <summary>The rows read last, oldest first.</summary>
    public List<(long RowId, string Id)> Tail { get; } = [];
}

/// <summary>How far into one log file reading has got. Confined to the store's thread.</summary>
internal sealed class AgentLogCursor
{
    public AgentLogCursor(string path, AgentProvider provider)
    {
        Path = path;
        Provider = provider;
    }

    public string Path { get; }
    public AgentProvider Provider { get; }
    /// <summary>The last offset read, or for a database the last rowid.</summary>
    public long Offset { get; set; }
    public string Identity { get; set; } = "";
    public AgentLineSplitter Splitter { get; } = new();
    public AgentLogState State { get; set; } = new();
    public DateTimeOffset Modified { get; set; } = AgentTime.DistantPast;
    public OpenCodeProgress OpenCode { get; set; } = new();
    /// <summary>Set when the log turned out replaced, cut short or written again after part of it was read. What its
    /// old contents gave is still counted, so its progress is not saved: the next launch reads it as rewritten.</summary>
    public bool Restarted { get; private set; }

    private (long Offset, string Identity, ulong Value)? fingerprinted;
    private string? codexParentLog;
    private int codexParentSearch = -1;

    public bool TracksTurns => AgentLogRouting.TracksTurns(Provider, Path, State);

    /// <summary>Where a resumed read picks up: the start of a line still being written, or past a line too long to keep.</summary>
    private long Boundary => Splitter.Discarding ? Offset : Offset - Splitter.PendingCount;

    /// <summary>Reads the log again from its start.</summary>
    public void StartOver(string identity)
    {
        if (Offset > 0) Restarted = true;
        Identity = identity;
        Offset = 0;
        Splitter.Reset();
        State = new AgentLogState();
        fingerprinted = null;
        codexParentSearch = -1;
    }

    /// <summary>The log whose turn a Codex subagent's responses count toward, looked for again when a log turns up
    /// or the one found goes away.</summary>
    public string? CodexParent(IReadOnlyDictionary<string, AgentLogCursor> cursors)
    {
        if (Provider != AgentProvider.Codex || State.CodexRoot.Length == 0 || TracksTurns) return null;
        if (codexParentSearch != cursors.Count || (codexParentLog is not null && !cursors.ContainsKey(codexParentLog)))
        {
            codexParentSearch = cursors.Count;
            codexParentLog = AgentLogRouting.CodexParentLog(State.CodexRoot,
                cursors.Values.Where(c => c.Provider == AgentProvider.Codex).Select(c => (c.Path, c.TracksTurns, c.State.Session, c.Modified)));
        }
        return codexParentLog;
    }

    public void FingerprintRead()
    {
        var end = Boundary;
        if (fingerprinted is { } taken && taken.Offset == end && taken.Identity == Identity) return;
        fingerprinted = AgentLogFiles.Fingerprint(Path, end) is { } value ? (end, Identity, value) : null;
    }

    /// <summary>False once the log no longer holds what was read: written again in place, perhaps past where reading
    /// stopped, which its size and identity alone do not show.</summary>
    public bool HoldsWhatWasRead =>
        fingerprinted is not { } taken || taken.Identity != Identity || AgentLogFiles.Fingerprint(Path, taken.Offset) == taken.Value;

    public AgentLogProgress Saved()
    {
        var end = Boundary;
        ulong value;
        if (fingerprinted is { } taken && taken.Offset == end && taken.Identity == Identity) value = taken.Value;
        else
        {
            value = AgentLogFiles.Fingerprint(Path, end) ?? 0;
            fingerprinted = (end, Identity, value);
        }
        return new AgentLogProgress(Path, Provider, end, Identity, Splitter.Discarding, Modified, State.Clone(), value);
    }

    /// <summary>A cursor resuming from saved progress, already checked with <see cref="AgentLogFiles.Intact"/>.</summary>
    public static AgentLogCursor Resumed(AgentLogProgress saved)
    {
        var cursor = new AgentLogCursor(saved.Path, saved.Provider)
        {
            Offset = saved.Offset, Identity = saved.Identity, Modified = saved.Modified, State = saved.State.Clone(),
        };
        cursor.Splitter.Discarding = saved.Discarding;
        cursor.fingerprinted = (saved.Offset, saved.Identity, saved.Fingerprint);
        return cursor;
    }
}

/// <summary>Finding and reading log files. Nothing here writes to a log.</summary>
internal static class AgentLogFiles
{
    private const FileShare ShareAll = FileShare.ReadWrite | FileShare.Delete;

    public static SafeFileHandle? OpenRead(string path)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, ShareAll, FileOptions.SequentialScan); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static DateTimeOffset? Modified(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The fingerprint of the log up to <paramref name="offset"/>; null when it cannot be read that far.</summary>
    public static ulong? Fingerprint(string path, long offset)
    {
        using var handle = OpenRead(path);
        if (handle is null || handle.IsInvalid) return null;
        var (head, tail) = AgentLogFingerprint.Ranges(offset);
        var first = new byte[head.Length];
        var last = new byte[tail.Length];
        try
        {
            if (ReadExactly(handle, first, head.Start) != head.Length || ReadExactly(handle, last, tail.Start) != tail.Length) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return AgentLogFingerprint.Hash(offset, first, last);
    }

    private static int ReadExactly(SafeFileHandle handle, byte[] buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(total), offset + total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>Whether saved progress still describes the log there: same file, holding the same bytes up to the offset.</summary>
    public static bool Intact(AgentLogProgress saved)
    {
        if (saved.Provider == AgentProvider.OpenCode) return false;
        return FileIdentity.Read(saved.Path) is { IsDirectory: false } facts && facts.Identity == saved.Identity
               && facts.Bytes >= saved.Offset && Fingerprint(saved.Path, saved.Offset) == saved.Fingerprint;
    }

    /// <summary>Log files changed since <paramref name="horizon"/>, newest last so live turns settle on the most recent
    /// state. Claude subagents come after every session, so the turn each one works for already stands.</summary>
    public static List<(string Path, AgentProvider Provider)> Discover(IEnumerable<AgentLogRoot> roots, DateTimeOffset horizon,
                                                                        CancellationToken token = default)
    {
        var found = new List<(string Path, AgentProvider Provider, DateTimeOffset Modified, bool Subagent)>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple, ReturnSpecialDirectories = false,
        };
        foreach (var root in roots.Where(r => r.Exists))
        {
            if (token.IsCancellationRequested) break;
            try
            {
                // OpenCode keeps its database beside the snapshots, clones and logs of its data folder, which are never walked.
                if (root.Provider == AgentProvider.OpenCode)
                {
                    var database = System.IO.Path.Combine(root.Path, OpenCodeRows.Database);
                    if (OpenCodeDatabase.Modified(database) is { } changed && changed >= horizon) found.Add((database, root.Provider, changed, false));
                    continue;
                }
                // A Copilot session can hold a whole workspace checkout; its log has one fixed shallow path.
                if (root.Provider == AgentProvider.Copilot)
                {
                    foreach (var session in new DirectoryInfo(root.Path).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }))
                    {
                        var log = new FileInfo(System.IO.Path.Combine(session.FullName, "events.jsonl"));
                        if (root.Accepts(log.FullName) && log.Exists && new DateTimeOffset(log.LastWriteTimeUtc, TimeSpan.Zero) is var modified && modified >= horizon)
                            found.Add((log.FullName, root.Provider, modified, false));
                    }
                    continue;
                }
                foreach (var file in new DirectoryInfo(root.Path).EnumerateFiles("*.jsonl", options))
                {
                    if (!root.Accepts(file.FullName)) continue;
                    var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                    if (modified < horizon) continue;
                    found.Add((file.FullName, root.Provider, modified,
                               root.Provider == AgentProvider.Claude && AgentLogRouting.ClaudeParent(file.FullName) is not null));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return found.OrderBy(f => f.Subagent).ThenBy(f => f.Modified).Select(f => (f.Path, f.Provider)).ToList();
    }

    /// <summary>Reads what was appended since the last call and hands over each complete line. A replaced or truncated
    /// file starts over. <paramref name="buffer"/> is reused between chunks.</summary>
    public static void ReadAppended(AgentLogCursor cursor, byte[] buffer, Func<bool> shouldContinue, Action<ReadOnlyMemory<byte>> line,
                                    Func<ReadOnlyMemory<byte>, bool>? including = null)
    {
        if (!shouldContinue()) return;
        if (FileIdentity.Read(cursor.Path) is not { IsDirectory: false } facts) return;
        var size = facts.Bytes;
        if (Modified(cursor.Path) is { } modified) cursor.Modified = modified;
        if (facts.Identity != cursor.Identity || size < cursor.Offset || (size > cursor.Offset && !cursor.HoldsWhatWasRead))
            cursor.StartOver(facts.Identity);
        try
        {
            if (size <= cursor.Offset) return;
            using var handle = OpenRead(cursor.Path);
            if (handle is null || handle.IsInvalid) return;
            while (cursor.Offset < size && shouldContinue())
            {
                var wanted = (int)Math.Min(buffer.Length, size - cursor.Offset);
                int read;
                try { read = RandomAccess.Read(handle, buffer.AsSpan(0, wanted), cursor.Offset); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
                if (read <= 0) break;
                cursor.Offset += read;
                cursor.Splitter.Feed(buffer.AsSpan(0, read), line, including);
            }
        }
        finally
        {
            cursor.FingerprintRead();
        }
    }
}
