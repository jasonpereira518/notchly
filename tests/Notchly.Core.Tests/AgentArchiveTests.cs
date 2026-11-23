// SPDX-License-Identifier: GPL-3.0-or-later
// The progress archive's layout and resume rules (upstream AgentUsageArchive.swift), the log fingerprint,
// Electron's safeStorage blobs as Cursor seals its token, and Claude's process records on Windows.
using System.Security.Cryptography;
using System.Text;
using Notchly.Core.Agents;

namespace Notchly.Core.Tests;

public class AgentArchiveTests
{
    private static DateTimeOffset U(double seconds) => AgentTime.FromUnixSeconds(seconds);

    private static AgentUsageArchive.Contents Sample()
    {
        var store = new AgentUsageStore();
        var record = new AgentUsageRecord(AgentProvider.Claude, U(1_800_000_000.25), "claude-opus-5-5", "app", "s1",
                                          new AgentTokens(10, 20, 30, 40, 5), 1.5, 0.25, 2);
        store.Apply([new AgentLogEntry.TurnBegan(U(1_800_000_000))], @"C:\logs\s1.jsonl", AgentProvider.Claude, true, U(1_800_000_000), U(1_800_000_001));
        store.Apply([new AgentLogEntry.Usage("k1", record, new AgentBillable(new AgentTokens(10, 20, 30, 40, 5), LongCacheWrite: 7, Fast: true))],
                    @"C:\logs\s1.jsonl", AgentProvider.Claude, true, U(1_800_000_000), U(1_800_000_001));
        store.Apply([new AgentLogEntry.Usage("k2", record with { Provider = AgentProvider.Codex, Cost = null, Model = "gpt-5.5" },
                                             new AgentBillable(new AgentTokens(1, 0, 0, 2), IsAggregate: true, WebSearches: 3))],
                    @"C:\logs\codex.jsonl", AgentProvider.Codex, true, U(1_800_000_000), U(1_800_000_001));
        store.UpdateLimits(new AgentLimits(AgentProvider.Codex,
            [new AgentLimitWindow("codex.300", AgentLimitKind.Session, 300, null, 42.5, U(1_800_010_000)),
             new AgentLimitWindow("codex.individual", AgentLimitKind.Other, null, "Scope", 7, null, U(1_800_020_000))],
            U(1_800_000_002), AgentLimitSource.SessionLog));
        store.Apply([new AgentLogEntry.Plan("pro", U(1_800_000_003))], @"C:\logs\codex.jsonl", AgentProvider.Codex, true, U(1_800_000_000), U(1_800_000_001));
        var state = new AgentLogState
        {
            Session = "s1", Project = "app", Model = "m", TurnOpen = true, LastTotal = new AgentTokens(1, 2, 3, 4, 5), CodexRoot = "root",
            CopilotTotals = new() { ["a"] = new AgentTokens(9) }, CopilotRequests = new() { ["b"] = 3 },
            CopilotRequestModels = new() { ["c"] = "d" }, CopilotTurnId = "t", CopilotFinalResponse = true,
        };
        var cursors = new List<AgentLogProgress>
        {
            new(@"C:\logs\s1.jsonl", AgentProvider.Claude, 1234, "0000abcd:0000000100000002", false, U(1_800_000_000), state, 0xDEADBEEF12345678),
            new(@"C:\logs\codex.jsonl", AgentProvider.Codex, 99, "0000abcd:0000000100000003", true, U(1_799_000_000), new AgentLogState(), 1),
        };
        return new AgentUsageArchive.Contents(new HashSet<AgentProvider> { AgentProvider.Claude, AgentProvider.Codex }, store.Save(), cursors);
    }

    [Fact]
    public void TheArchiveReadsBackWhatWasSavedAndNothingElse()
    {
        var contents = Sample();
        var bytes = AgentUsageArchive.Encode(contents, "build-1");
        Assert.Equal("NAUA"u8.ToArray(), bytes[..4]);
        var decoded = AgentUsageArchive.Decode(bytes, "build-1");
        Assert.NotNull(decoded);
        Assert.True(decoded.Providers.SetEquals(contents.Providers));
        Assert.True(AgentUsageArchive.Same(decoded.Store, contents.Store));
        Assert.Equal(2, decoded.Store.Records.Count);
        Assert.Equal("pro", decoded.Store.CodexPlan);
        Assert.Single(decoded.Store.Turns);
        Assert.Equal(contents.Cursors.Count, decoded.Cursors.Count);
        for (var i = 0; i < contents.Cursors.Count; i++)
        {
            var (a, b) = (contents.Cursors[i], decoded.Cursors[i]);
            Assert.Equal((a.Path, a.Provider, a.Offset, a.Identity, a.Discarding, a.Modified, a.Fingerprint),
                         (b.Path, b.Provider, b.Offset, b.Identity, b.Discarding, b.Modified, b.Fingerprint));
            Assert.Equal((a.State.Session, a.State.Project, a.State.Model, a.State.TurnOpen, a.State.LastTotal, a.State.CodexRoot,
                          a.State.CopilotTurnId, a.State.CopilotFinalResponse),
                         (b.State.Session, b.State.Project, b.State.Model, b.State.TurnOpen, b.State.LastTotal, b.State.CodexRoot,
                          b.State.CopilotTurnId, b.State.CopilotFinalResponse));
            Assert.Equal(a.State.CopilotTotals, b.State.CopilotTotals);
            Assert.Equal(a.State.CopilotRequests, b.State.CopilotRequests);
            Assert.Equal(a.State.CopilotRequestModels, b.State.CopilotRequestModels);
        }
        // Encoding is deterministic, so an unchanged store saves the same bytes.
        Assert.Equal(bytes, AgentUsageArchive.Encode(decoded, "build-1"));
    }

    [Fact]
    public void ADamagedOrForeignArchiveReadsAsNothing()
    {
        var bytes = AgentUsageArchive.Encode(Sample(), "build-1");
        Assert.Null(AgentUsageArchive.Decode(bytes, "build-2"));
        for (var i = 4; i < bytes.Length; i += Math.Max(1, bytes.Length / 50))
        {
            var damaged = (byte[])bytes.Clone();
            damaged[i] ^= 0x40;
            Assert.Null(AgentUsageArchive.Decode(damaged, "build-1"));
        }
        Assert.Null(AgentUsageArchive.Decode(bytes[..^1], "build-1"));
        Assert.Null(AgentUsageArchive.Decode("VAUA"u8.ToArray().Concat(bytes[4..]).ToArray(), "build-1"));
        Assert.Null(AgentUsageArchive.Decode([], "build-1"));
    }

    [Fact]
    public void ResumingGivesBackWhatALogGoneOrRewrittenGave()
    {
        var contents = Sample();
        var all = contents.Cursors.Select(c => c.Path).ToHashSet();
        var intact = AgentUsageArchive.Resume(contents, all, _ => true, U(1_700_000_000));
        Assert.Equal(2, intact.Cursors.Count);
        Assert.Equal(2, intact.Store.Records.Count);

        // The Codex log was rewritten: its response goes, the Claude one stays.
        var rewritten = AgentUsageArchive.Resume(contents, all, p => p.Provider != AgentProvider.Codex, U(1_700_000_000));
        Assert.False(rewritten.Unchanged);
        Assert.Equal(@"C:\logs\s1.jsonl", Assert.Single(rewritten.Cursors).Path);
        Assert.Equal(AgentProvider.Claude, Assert.Single(rewritten.Store.Records).Provider);
        // Limits and the plan describe the account, not the log.
        Assert.Equal("pro", rewritten.Store.CodexPlan);
        Assert.True(rewritten.Store.Limits.ContainsKey(AgentProvider.Codex));

        // The Claude log is gone: its turn ends without a notice.
        var gone = AgentUsageArchive.Resume(contents, new HashSet<string> { @"C:\logs\codex.jsonl" }, _ => true, U(1_700_000_000));
        Assert.Empty(gone.Store.Live);
        Assert.Equal(AgentProvider.Codex, Assert.Single(gone.Store.Records).Provider);
    }

    [Fact]
    public void TheFingerprintCoversTheOffsetAndBothEnds()
    {
        Assert.Equal(((0L, 0), (0L, 0)), AgentLogFingerprint.Ranges(0));
        Assert.Equal(((0L, 100), (0L, 100)), AgentLogFingerprint.Ranges(100));
        Assert.Equal(((0L, 4096), (5904L, 4096)), AgentLogFingerprint.Ranges(10_000));
        var head = Encoding.UTF8.GetBytes("{\"a\":1}\n");
        var tail = Encoding.UTF8.GetBytes("{\"b\":2}\n");
        var hash = AgentLogFingerprint.Hash(16, head, tail);
        Assert.Equal(hash, AgentLogFingerprint.Hash(16, head, tail));
        Assert.NotEqual(hash, AgentLogFingerprint.Hash(17, head, tail));
        Assert.NotEqual(hash, AgentLogFingerprint.Hash(16, tail, head));
        // FNV-1a of the eight zero bytes of offset 0, as upstream hashes an empty log.
        var expected = 0xCBF29CE484222325UL;
        for (var i = 0; i < 8; i++) expected = (expected ^ 0) * 0x100000001B3UL;
        Assert.Equal(expected, AgentLogFingerprint.Hash(0, [], []));
    }

    [Fact]
    public void ElectronSafeStorageOpensWhatItSealedWithItsKeyOnly()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"HS256"}""")).TrimEnd('=');
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"auth0|user_123"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = $"{header}.{payload}.c2lnbmF0dXJl";
        var blob = ElectronSafeStorage.Seal(Encoding.UTF8.GetBytes(jwt), key, nonce);
        Assert.Equal("v10"u8.ToArray(), blob[..3]);
        Assert.Equal(jwt, Encoding.UTF8.GetString(ElectronSafeStorage.Open(blob, key)!));
        Assert.Null(ElectronSafeStorage.Open(blob, RandomNumberGenerator.GetBytes(32)));
        var tampered = (byte[])blob.Clone();
        tampered[^1] ^= 1;
        Assert.Null(ElectronSafeStorage.Open(tampered, key));
        Assert.Null(ElectronSafeStorage.Open(blob[..10], key));

        // Through CursorToken, as the app passes the unseal function.
        var stored = CursorToken.SealedPrefix + Convert.ToBase64String(blob);
        Assert.Equal(jwt, CursorToken.Jwt(stored, sealedBytes => ElectronSafeStorage.Open(sealedBytes, key)));
        Assert.Equal("WorkosCursorSessionToken=user_123%3A%3A" + jwt, CursorToken.Cookie(jwt));

        // Local State keeps the DPAPI-sealed key behind "DPAPI".
        var sealedKey = new byte[] { 1, 2, 3 };
        var localState = $$$"""{"os_crypt":{"encrypted_key":"{{{Convert.ToBase64String([.. "DPAPI"u8.ToArray(), .. sealedKey])}}}"}}""";
        Assert.Equal(sealedKey, ElectronSafeStorage.SealedKey(localState));
        Assert.Null(ElectronSafeStorage.SealedKey($$$"""{"os_crypt":{"encrypted_key":"{{{Convert.ToBase64String(sealedKey)}}}"}}"""));
        Assert.Null(ElectronSafeStorage.SealedKey("{}"));
    }

    [Fact]
    public void ClaudeRecordsOnWindowsMatchTheComputerInAnyCaseAndPassTheirStartTime()
    {
        var records = new string?[]
        {
            """{"pid":10,"sessionId":"here","pidDomain":"win32:jasonlaptop","procStart":"134398770077858280"}""",
            """{"pid":11,"sessionId":"elsewhere","pidDomain":"win32:otherpc","procStart":"1"}""",
            """{"pid":12,"sessionId":"reused","pidDomain":"WIN32:JASONLAPTOP","procStart":"5"}""",
            """{"pid":13,"sessionId":"old"}""",
        };
        var seen = new List<(int, string?)>();
        var registry = AgentSessionRegistry.FromRecords(records, (pid, start) =>
        {
            seen.Add((pid, start));
            return start == "134398770077858280" || (start is null && pid == 13);
        }, listed: true, "win32:JASONLAPTOP");
        Assert.True(registry.Running.SetEquals(["here", "old"]));
        Assert.True(registry.Ended.SetEquals(["reused"]));
        Assert.Equal([(10, "134398770077858280"), (12, "5"), (13, null)], seen);
    }
}
