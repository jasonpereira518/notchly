// SPDX-License-Identifier: GPL-3.0-or-later
// Port of upstream Sources/Vorssaint/Services/AgentUsage/AgentUsageArchive.swift (the saved counters and log
// progress, a compact binary layout with shared strings and a checksum, and how a launch resumes from it) and
// the pure half of AgentUsageStore.swift's AgentLogReader.fingerprint. Windows names a file by its volume serial
// and file index (text, from the app's FileIdentity) where macOS uses the inode; the magic is "NAUA".
using System.Buffers.Binary;
using System.Text;

namespace Notchly.Core.Agents;

/// <summary>Where reading one log stopped, at a line boundary: a line still being written is read again whole.</summary>
/// <param name="Identity">The file's volume serial and index, which tell a replaced file apart.</param>
/// <param name="Fingerprint">Tells the log that was read from one rewritten in place, which keeps its identity
/// (<see cref="AgentLogFingerprint"/>).</param>
public sealed record AgentLogProgress(string Path, AgentProvider Provider, long Offset, string Identity, bool Discarding,
                                      DateTimeOffset Modified, AgentLogState State, ulong Fingerprint);

/// <summary>A hash of a log's first and last few kilobytes before an offset, and of the offset itself. A log
/// rewritten with a different start, or different lines just before where reading stopped, no longer matches.</summary>
public static class AgentLogFingerprint
{
    /// <summary>Bytes hashed at each end.</summary>
    public const int Span = 4096;

    /// <summary>The two ranges to read for <paramref name="offset"/>: from the start, and just before it.</summary>
    public static ((long Start, int Length) Head, (long Start, int Length) Tail) Ranges(long offset)
    {
        var length = (int)Math.Min(Span, Math.Max(0, offset));
        return ((0, length), (Math.Max(0, offset) - length, length));
    }

    /// <summary>FNV-1a over the offset (little-endian) and both ranges: stable across launches.</summary>
    public static ulong Hash(long offset, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
    {
        Span<byte> mark = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(mark, (ulong)offset);
        return AgentUsageArchive.Fnv1a(tail, AgentUsageArchive.Fnv1a(head, AgentUsageArchive.Fnv1a(mark)));
    }
}

/// <summary>
/// The counters read so far and where reading stopped in each log, so a launch reads only what the agents wrote
/// since. It holds what the store holds in memory and nothing more: counters, model names, times and folder names,
/// never a prompt, reply, tool output, email or token. A checksum covers everything after the magic, and every value
/// read back is checked; anything unexpected, including a file another build wrote, reads as nothing.
/// </summary>
public static class AgentUsageArchive
{
    public sealed record Contents(IReadOnlySet<AgentProvider> Providers, AgentUsageStore.Saved Store, IReadOnlyList<AgentLogProgress> Cursors);

    public const string FileName = "agent-usage.bin";
    private static readonly byte[] Magic = "NAUA"u8.ToArray();
    public const int Format = 1;

    /// <summary>The parser and the store change between builds; what one build read is not taken for what another
    /// would have. Deterministic builds give the same code the same module version id.</summary>
    public static string Build => $"{AppIdentity.Version} {typeof(AgentUsageStore).Assembly.ManifestModule.ModuleVersionId:N}";

    internal static ulong Fnv1a(ReadOnlySpan<byte> bytes, ulong hash = 0xCBF2_9CE4_8422_2325)
    {
        foreach (var b in bytes) hash = (hash ^ b) * 0x100_0000_01B3;
        return hash;
    }

    /// <summary>
    /// The store and cursors a launch resumes with, given the logs <paramref name="found"/> now, left as reading every
    /// log from its start would leave them. A log gone, replaced or rewritten since (<paramref name="intact"/> false)
    /// gives back what it gave and is read from its start again; a response it shared with another log kept the
    /// largest count either gave, so those logs are read again too. Limits and the plan stay: they describe the
    /// account. <c>Unchanged</c> tells whether the result is still what was saved.
    /// </summary>
    public static (AgentUsageStore Store, IReadOnlyList<AgentLogProgress> Cursors, bool Unchanged) Resume(
        Contents contents, IReadOnlySet<string> found, Func<AgentLogProgress, bool> intact, DateTimeOffset horizon)
    {
        var store = AgentUsageStore.Restore(contents.Store);
        var cursors = new Dictionary<string, AgentLogProgress>(StringComparer.Ordinal);
        foreach (var saved in contents.Cursors)
            if (found.Contains(saved.Path) && intact(saved)) cursors[saved.Path] = saved;
        var gone = store.Files.Where(f => !cursors.ContainsKey(f)).ToHashSet(StringComparer.Ordinal);
        var sharing = store.FilesSharing(gone).Where(f => !gone.Contains(f)).ToList();
        foreach (var file in sharing) cursors.Remove(file);
        gone.UnionWith(sharing);
        store.Forget(gone);
        // Prices may be newer than the ones the counts were saved with.
        store.Reprice();
        store.DropRecords(horizon);
        var unchanged = cursors.Count == contents.Cursors.Count && Same(store.Save(), contents.Store);
        return (store, [.. cursors.Values], unchanged);
    }

    /// <summary>Whether two saved stores hold the same values.</summary>
    public static bool Same(AgentUsageStore.Saved a, AgentUsageStore.Saved b) =>
        a.Records.Count == b.Records.Count
        && a.Records.Zip(b.Records).All(p => p.First.Key == p.Second.Key && p.First.Record == p.Second.Record
                                             && p.First.Billable == p.Second.Billable && p.First.Sources.SequenceEqual(p.Second.Sources))
        && a.Limits.SequenceEqual(b.Limits) && a.CodexPlan == b.CodexPlan && a.CodexPlanObserved == b.CodexPlanObserved
        && a.Turns.SequenceEqual(b.Turns) && a.Waiting.SequenceEqual(b.Waiting);

    // MARK: Layout

    public static byte[] Encode(Contents contents, string build)
    {
        var body = new Writer();
        body.String(build);
        body.Count(contents.Providers.Count);
        foreach (var provider in contents.Providers.OrderBy(p => p.RawValue(), StringComparer.Ordinal)) body.Provider(provider);
        var store = contents.Store;
        body.Count(store.Records.Count);
        foreach (var entry in store.Records)
        {
            body.String(entry.Key);
            body.Record(entry.Record);
            // The billed tokens are nearly always the recorded ones.
            var same = entry.Billable.Tokens == entry.Record.Tokens;
            body.Bool(same);
            if (!same) body.Tokens(entry.Billable.Tokens);
            body.Bool(entry.Billable.IsAggregate);
            body.Int(entry.Billable.LongCacheWrite);
            body.Bool(entry.Billable.Fast);
            body.Bool(entry.Billable.Domestic);
            body.Int(entry.Billable.WebSearches);
            body.Count(entry.Sources.Count);
            foreach (var source in entry.Sources) body.String(source);
        }
        body.Count(store.Limits.Count);
        foreach (var limits in store.Limits) body.Limits(limits);
        body.Bool(store.CodexPlan is not null);
        if (store.CodexPlan is { } plan) body.String(plan);
        body.Date(store.CodexPlanObserved);
        foreach (var sessions in new[] { store.Turns, store.Waiting })
        {
            body.Count(sessions.Count);
            foreach (var session in sessions) body.Session(session);
        }
        body.Count(contents.Cursors.Count);
        foreach (var cursor in contents.Cursors)
        {
            body.String(cursor.Path);
            body.Provider(cursor.Provider);
            body.Int(cursor.Offset);
            body.String(cursor.Identity);
            body.Bool(cursor.Discarding);
            body.Date(cursor.Modified);
            body.State(cursor.State);
            body.Unsigned(cursor.Fingerprint);
        }
        var header = new Writer();
        header.Int(Format);
        header.Count(body.Strings.Count);
        foreach (var text in body.Strings)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            header.Count(bytes.Length);
            header.Bytes.AddRange(bytes);
        }
        var data = new List<byte>(Magic.Length + header.Bytes.Count + body.Bytes.Count + 8);
        data.AddRange(Magic);
        data.AddRange(header.Bytes);
        data.AddRange(body.Bytes);
        var sum = Fnv1a(body.Bytes.ToArray(), Fnv1a(header.Bytes.ToArray()));
        var tail = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, sum);
        data.AddRange(tail);
        return [.. data];
    }

    /// <summary>The contents of <paramref name="data"/>, or null when anything about it is unexpected.</summary>
    public static Contents? Decode(ReadOnlySpan<byte> data, string build)
    {
        if (data.Length <= Magic.Length + 8 || !data[..Magic.Length].SequenceEqual(Magic)) return null;
        var payload = data[Magic.Length..^8].ToArray();
        if (Fnv1a(payload) != BinaryPrimitives.ReadUInt64LittleEndian(data[^8..])) return null;
        var reader = new Reader(payload);
        try
        {
            if (reader.Int() != Format) return null;
            var strings = reader.Count();
            for (var i = 0; i < strings; i++) reader.Strings.Add(new UTF8Encoding(false, true).GetString(reader.Take(reader.Count())));
            if (reader.String() != build) return null;
            var providers = new HashSet<AgentProvider>();
            for (int i = 0, n = reader.Count(); i < n; i++) providers.Add(reader.Provider());
            var records = new List<AgentUsageStore.SavedRecord>();
            for (int i = 0, n = reader.Count(); i < n; i++)
            {
                var key = reader.String();
                var record = reader.Record();
                var tokens = reader.Bool() ? record.Tokens : reader.Tokens();
                var billable = new AgentBillable(tokens, reader.Bool(), reader.Amount(), reader.Bool(), reader.Bool(), reader.Amount());
                var sources = new List<string>();
                for (int j = 0, m = reader.Count(); j < m; j++) sources.Add(reader.String());
                records.Add(new AgentUsageStore.SavedRecord(key, record, billable, sources));
            }
            var limits = new List<AgentLimits>();
            for (int i = 0, n = reader.Count(); i < n; i++) limits.Add(reader.Limits());
            var codexPlan = reader.Bool() ? reader.String() : null;
            var observed = reader.Date();
            var turns = new List<AgentLiveSession>();
            for (int i = 0, n = reader.Count(); i < n; i++) turns.Add(reader.Session());
            var waiting = new List<AgentLiveSession>();
            for (int i = 0, n = reader.Count(); i < n; i++) waiting.Add(reader.Session());
            var cursors = new List<AgentLogProgress>();
            for (int i = 0, n = reader.Count(); i < n; i++)
            {
                var path = reader.String();
                var provider = reader.Provider();
                var offset = reader.Amount();
                cursors.Add(new AgentLogProgress(path, provider, offset, reader.String(), reader.Bool(), reader.Date(), reader.State(), reader.Unsigned()));
            }
            if (!reader.AtEnd) return null;
            return new Contents(providers, new AgentUsageStore.Saved(records, limits, codexPlan, observed, turns, waiting), cursors);
        }
        catch (FormatException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    // MARK: Writing

    private sealed class Writer
    {
        public readonly List<byte> Bytes = [];
        public readonly List<string> Strings = [];
        private readonly Dictionary<string, int> positions = new(StringComparer.Ordinal);

        public void Unsigned(ulong value)
        {
            while (value >= 0x80)
            {
                Bytes.Add((byte)(value & 0x7F | 0x80));
                value >>= 7;
            }
            Bytes.Add((byte)value);
        }

        public void Int(long value) => Unsigned((ulong)((value << 1) ^ (value >> 63)));
        public void Count(int value) => Unsigned((ulong)value);
        public void Bool(bool value) => Bytes.Add(value ? (byte)1 : (byte)0);

        public void Double(double value)
        {
            Span<byte> raw = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(raw, value);
            foreach (var b in raw) Bytes.Add(b);
        }

        public void Date(DateTimeOffset value) => Int(value.UtcTicks);

        public void String(string value)
        {
            if (positions.TryGetValue(value, out var position))
            {
                Count(position);
                return;
            }
            positions[value] = Strings.Count;
            Count(Strings.Count);
            Strings.Add(value);
        }

        public void Provider(AgentProvider value) => String(value.RawValue());

        public void Tokens(AgentTokens value)
        {
            foreach (var part in new[] { value.Input, value.CacheWrite, value.CacheRead, value.Output, value.Reasoning }) Int(part);
        }

        public void Record(AgentUsageRecord value)
        {
            Provider(value.Provider);
            Date(value.Date);
            String(value.Model);
            String(value.Project);
            String(value.Session);
            Int(value.Requests);
            Tokens(value.Tokens);
            Bool(value.Cost is not null);
            if (value.Cost is { } cost) Double(cost);
            Double(value.Savings);
            Bool(value.ReportedCost);
        }

        public void Limits(AgentLimits value)
        {
            Provider(value.Provider);
            Count(value.Windows.Count);
            foreach (var window in value.Windows)
            {
                String(window.Id);
                Count((int)window.Kind);
                Bool(window.Minutes is not null);
                if (window.Minutes is { } minutes) Int(minutes);
                Bool(window.Scope is not null);
                if (window.Scope is { } scope) String(scope);
                Double(window.UsedPercent);
                Bool(window.ResetsAt is not null);
                if (window.ResetsAt is { } resets) Date(resets);
                Bool(window.EstimatedResetsAt is not null);
                if (window.EstimatedResetsAt is { } estimated) Date(estimated);
            }
            Date(value.ObservedAt);
            Count((int)value.Source);
        }

        public void Session(AgentLiveSession value)
        {
            String(value.Id);
            Provider(value.Provider);
            Date(value.Started);
            Date(value.LastActivity);
            String(value.Model);
            String(value.Project);
            Tokens(value.Tokens);
            Double(value.Cost);
        }

        private void Map<T>(IReadOnlyDictionary<string, T> values, Action<T> write)
        {
            Count(values.Count);
            foreach (var key in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                String(key);
                write(values[key]);
            }
        }

        public void State(AgentLogState value)
        {
            String(value.Session);
            String(value.Project);
            String(value.Model);
            Bool(value.TurnOpen);
            Bool(value.SawUsageRecords);
            Bool(value.LastTotal is not null);
            if (value.LastTotal is { } total) Tokens(total);
            Bool(value.Fast);
            Bool(value.CodexSubagent);
            Bool(value.CodexHeaderRead);
            String(value.CodexRoot);
            String(value.CodexTotalsSession);
            Map(value.CopilotTotals, Tokens);
            Map(value.CopilotRequests, v => Int(v));
            Map(value.CopilotRequestModels, String);
            Map(value.CopilotReportedRequests, v => Int(v));
            Bool(value.CopilotTurnId is not null);
            if (value.CopilotTurnId is { } turn) String(turn);
            Bool(value.CopilotFinalResponse);
        }
    }

    // MARK: Reading

    private sealed class Reader(byte[] bytes)
    {
        private int position;
        public readonly List<string> Strings = [];

        public bool AtEnd => position == bytes.Length;

        private byte Byte()
        {
            if (position >= bytes.Length) throw new FormatException();
            return bytes[position++];
        }

        public byte[] Take(int length)
        {
            if (length > bytes.Length - position) throw new FormatException();
            var slice = bytes.AsSpan(position, length).ToArray();
            position += length;
            return slice;
        }

        public ulong Unsigned()
        {
            ulong value = 0;
            var shift = 0;
            while (true)
            {
                var next = Byte();
                if (shift >= 64) throw new FormatException();
                value |= (ulong)(next & 0x7F) << shift;
                if (next < 0x80) return value;
                shift += 7;
            }
        }

        public long Int()
        {
            var raw = Unsigned();
            return (long)(raw >> 1) ^ -(long)(raw & 1);
        }

        /// <summary>A count never exceeds the bytes left, which keeps a damaged file from asking for an enormous allocation.</summary>
        public int Count()
        {
            var value = Unsigned();
            if (value > (ulong)(bytes.Length - position)) throw new FormatException();
            return (int)value;
        }

        public bool Bool() => Byte() switch
        {
            0 => false,
            1 => true,
            _ => throw new FormatException(),
        };

        public double Double()
        {
            var value = BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
            if (!double.IsFinite(value)) throw new FormatException();
            return value;
        }

        public DateTimeOffset Date()
        {
            var ticks = Int();
            if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks) throw new FormatException();
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        public string String()
        {
            var index = Unsigned();
            if (index >= (ulong)Strings.Count) throw new FormatException();
            return Strings[(int)index];
        }

        public AgentProvider Provider() => AgentProviders.FromRawValue(String()) ?? throw new FormatException();

        /// <summary>Counts are never negative in a log.</summary>
        public long Amount()
        {
            var value = Int();
            if (value < 0) throw new FormatException();
            return value;
        }

        private int SmallAmount()
        {
            var value = Amount();
            if (value > int.MaxValue) throw new FormatException();
            return (int)value;
        }

        public AgentTokens Tokens() => new(Amount(), Amount(), Amount(), Amount(), Amount());

        private T Enum<T>(int count) where T : struct, System.Enum
        {
            var raw = Unsigned();
            if (raw >= (ulong)count) throw new FormatException();
            return (T)(object)(int)raw;
        }

        public AgentUsageRecord Record()
        {
            var provider = Provider();
            var date = Date();
            var model = String();
            var project = String();
            var session = String();
            var requests = SmallAmount();
            var tokens = Tokens();
            double? cost = Bool() ? Double() : null;
            var savings = Double();
            return new AgentUsageRecord(provider, date, model, project, session, tokens, cost, savings, requests, Bool());
        }

        public AgentLimits Limits()
        {
            var provider = Provider();
            var windows = new List<AgentLimitWindow>();
            for (int i = 0, n = Count(); i < n; i++)
            {
                var id = String();
                var kind = Enum<AgentLimitKind>(3);
                int? minutes = Bool() ? (int)Math.Clamp(Int(), int.MinValue, int.MaxValue) : null;
                var scope = Bool() ? String() : null;
                var used = Double();
                DateTimeOffset? resets = Bool() ? Date() : null;
                DateTimeOffset? estimated = Bool() ? Date() : null;
                windows.Add(new AgentLimitWindow(id, kind, minutes, scope, used, resets, estimated));
            }
            var observed = Date();
            return new AgentLimits(provider, windows, observed, Enum<AgentLimitSource>(3));
        }

        public AgentLiveSession Session() =>
            new(String(), Provider(), Date(), Date(), String(), String(), Tokens(), Double());

        private Dictionary<string, T> Map<T>(Func<T> read)
        {
            var values = new Dictionary<string, T>();
            for (int i = 0, n = Count(); i < n; i++)
            {
                var key = String();
                if (!values.TryAdd(key, read())) throw new FormatException();
            }
            return values;
        }

        public AgentLogState State()
        {
            var state = new AgentLogState
            {
                Session = String(), Project = String(), Model = String(), TurnOpen = Bool(), SawUsageRecords = Bool(),
            };
            state.LastTotal = Bool() ? Tokens() : null;
            state.Fast = Bool();
            state.CodexSubagent = Bool();
            state.CodexHeaderRead = Bool();
            state.CodexRoot = String();
            state.CodexTotalsSession = String();
            state.CopilotTotals = Map(Tokens);
            state.CopilotRequests = Map(SmallAmount);
            state.CopilotRequestModels = Map(String);
            state.CopilotReportedRequests = Map(SmallAmount);
            state.CopilotTurnId = Bool() ? String() : null;
            state.CopilotFinalResponse = Bool();
            return state;
        }
    }
}
