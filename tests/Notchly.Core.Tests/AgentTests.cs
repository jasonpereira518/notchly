// SPDX-License-Identifier: GPL-3.0-or-later
// Ports of the contracts in upstream Tests/NotchAgentTests.swift (pricing, the price list, Claude, Codex and
// OpenCode logs, summaries, limits, live turns, the strip, preferences, formatting), AgentClaudeServerTests.swift
// (limits, intervals) and CopilotAgentTests.swift, with the same numbers; plus the Windows-only Cursor helpers.
// Every log line is synthetic. SQLite-backed upstream cases are ported as the rows the reader would hand over.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Notchly.Core.Agents;

namespace Notchly.Core.Tests;

/// <summary>Pricing is a process-wide list, so every agent test runs in one collection.</summary>
[CollectionDefinition("AgentPricing", DisableParallelization = true)]
public class AgentPricingCollection;

[Collection("AgentPricing")]
public class AgentTests
{
    private static readonly AgentPriceList Shipped = AgentPriceSource.Bundled()!;
    private static readonly DateTimeOffset Zero = AgentTime.FromUnixSeconds(0);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly IReadOnlyDictionary<AgentProvider, AgentLimits> NoLimits = new Dictionary<AgentProvider, AgentLimits>();
    private static readonly IReadOnlyDictionary<AgentProvider, AgentPlan> NoPlans = new Dictionary<AgentProvider, AgentPlan>();

    private static DateTimeOffset T(string iso) => AgentTimestamp.Parse(iso)!.Value;
    private static DateTimeOffset U(double seconds) => AgentTime.FromUnixSeconds(seconds);
    private static HashSet<AgentProvider> Set(params AgentProvider[] providers) => [.. providers];

    private static IReadOnlyList<AgentLogEntry> Claude(string line, AgentLogState state, DateTimeOffset now) => new ClaudeReader().Parse(line, state, now);
    private static IReadOnlyList<AgentLogEntry> Codex(string line, AgentLogState state, DateTimeOffset now) => new CodexReader().Parse(line, state, now);
    private static IReadOnlyList<AgentLogEntry> OpenCode(string line, AgentLogState state, DateTimeOffset now) => new OpenCodeReader().Parse(line, state, now);

    private static string ClaudeAssistant(string id = "msg_1", string request = "req_1", string model = "claude-opus-5-5", string stop = "tool_use",
                                          int output = 225, string time = "2026-09-21T23:42:45.078Z", bool sidechain = false,
                                          string cwd = "/Users/me/code/app") =>
        $$$$"""{"parentUuid":"p","isSidechain":{{{{(sidechain ? "true" : "false")}}}},"type":"assistant","timestamp":"{{{{time}}}}","sessionId":"s1","cwd":"{{{{cwd}}}}","version":"2.1.280","requestId":"{{{{request}}}}","message":{"id":"{{{{id}}}}","model":"{{{{model}}}}","role":"assistant","stop_reason":"{{{{stop}}}}","content":[{"type":"text","text":"\"type\":\"assistant\""}],"usage":{"input_tokens":2,"cache_creation_input_tokens":17218,"cache_read_input_tokens":43134,"output_tokens":{{{{output}}}},"output_tokens_details":{"thinking_tokens":32},"cache_creation":{"ephemeral_1h_input_tokens":17218,"ephemeral_5m_input_tokens":0},"server_tool_use":{"web_search_requests":0},"speed":"standard","inference_geo":"not_available"}}}""";

    private static string ClaudeUser(string content = "Fix the build", string time = "2026-09-21T23:40:00.000Z", bool meta = false, bool toolResult = false)
    {
        var body = toolResult ? """[{"type":"tool_result","content":"ok"}]},"toolUseResult":{"stdout":"ok"}""" : System.Text.Json.JsonSerializer.Serialize(content, Relaxed) + "}";
        return $$"""{"type":"user","timestamp":"{{time}}","sessionId":"s1","cwd":"/Users/me/code/app","isMeta":{{(meta ? "true" : "false")}},"message":{"role":"user","content":{{body}}}""";
    }

    private static readonly System.Text.Json.JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static AgentTokens ReplyTokens => new(2, 17_218, 43_134, 225);

    private static AgentLogEntry.Usage FirstUsage(IEnumerable<AgentLogEntry> entries) => entries.OfType<AgentLogEntry.Usage>().First();
    private static bool Ended(IEnumerable<AgentLogEntry> entries) => entries.Any(e => e is AgentLogEntry.TurnEnded);
    private static bool FinishedBy(IEnumerable<AgentUsageEvent> events, AgentProvider provider) =>
        events.Any(e => e is AgentUsageEvent.Finished f && f.Provider == provider);

    // MARK: Pricing

    [Fact]
    public void ClaudeModelsBillEveryTokenCategoryAtTheirOwnRates()
    {
        var opus = AgentPricing.Cost(new AgentBillable(new AgentTokens(1000, 10_000, 100_000, 2000), LongCacheWrite: 6000), "claude-opus-5-5");
        Assert.Equal(0.132, opus.Cost!.Value, 6);
        Assert.Equal(100_000 * (4 - 0.2) / 1_000_000, opus.Savings, 9);
        var sonnet = AgentPricing.Cost(new AgentBillable(new AgentTokens(1000, 0, 100_000, 2000)), "claude-sonnet-5-5");
        Assert.Equal(0.032, sonnet.Cost!.Value, 9);
        Assert.Equal(0.19, sonnet.Savings, 9);
        // Haiku 5.5 counts every prompt token and charges five times past 100K.
        var haiku = new AgentBillable(new AgentTokens(1000, 4000, 95_000, 2000), LongCacheWrite: 3000);
        Assert.Equal(0.002775, AgentPricing.Cost(haiku, "anthropic.claude-haiku-5-5").Cost!.Value, 8);
        haiku = haiku with { Tokens = haiku.Tokens with { Input = 1001 } };
        Assert.Equal(0.0138755, AgentPricing.Cost(haiku, "claude-haiku-5-5").Cost!.Value, 8);
        Assert.Equal(0.0027751, AgentPricing.Cost(haiku with { IsAggregate = true }, "claude-haiku-5-5").Cost!.Value, 8);
    }

    [Fact]
    public void FamiliesMatchAtAWordBoundaryAndSiblingsHaveNoPrice()
    {
        Assert.Equal(4, AgentPricing.Price("claude-opus-5-5")?.Input);
        Assert.Equal(4, AgentPricing.Price("claude-opus-5.5")?.Input);
        Assert.Equal(5, AgentPricing.Price("claude-opus-5")?.Input);
        Assert.Equal(15, AgentPricing.Price("claude-opus-4-1-20250805")?.Input);
        Assert.Equal(5, AgentPricing.Price("claude-opus-4-5-20251101")?.Input);
        Assert.Equal(3, AgentPricing.Price("us.anthropic.claude-sonnet-4-5-20250929-v1:0")?.Input);
        Assert.Equal(5, AgentPricing.Price("claude-haiku-4-5@20251001")?.Output);
        Assert.Equal(3, AgentPricing.Price("claude-sonnet-4-5[1m]")?.Input);
        Assert.Equal(1.75, AgentPricing.Price("gpt-5.2-codex")?.Input);
        Assert.Equal(1.25, AgentPricing.Price("gpt-5")?.Input);
        Assert.Equal(50, AgentPricing.Price("gpt-6-astra")?.Output);
        foreach (var unknown in new[] { "gpt-6-astra-mini", "gpt-reserve", "gpt-6-astra-deep-research", "gpt-6-sol-cyber", "codex-auto-review",
                                        "claude-opus-5-6", "claude-sonnet-5-6", "claude-opus-6", "gpt-7", "gpt-6-sol-2" })
            Assert.Null(AgentPricing.Price(unknown));
        Assert.Null(AgentPricing.Cost(new AgentBillable(new AgentTokens(10)), "gpt-reserve").Cost);
        Assert.Equal(5, AgentPricing.Price("claude-opus-5-20260101")?.Input);
        Assert.Equal(3, AgentPricing.Price("claude-3-5-sonnet-latest")?.Input);
        Assert.Equal(10, AgentPricing.Price("gpt-6-astra-2026-10-01")?.Input);
        Assert.Equal(1.25, AgentPricing.Price("gpt-5-2025-08-07")?.Input);
        Assert.Equal(12.5, AgentPricing.Price("gpt-5.5-cyber")?.Input);
        Assert.Equal(0.75, AgentPricing.Price("gpt-5.4-mini")?.Input);
        Assert.Equal(125, AgentPricing.Price("claude-mythos-preview")?.Output);
        Assert.Equal(75, AgentPricing.Price("gpt-daybreak-red-latest")?.Output);
        Assert.Equal(10, AgentPricing.Price("gpt-4-0125-preview")?.Input);
        Assert.Equal(30, AgentPricing.Price("gpt-4-1106-vision-preview")?.Output);
        Assert.Equal(5, AgentPricing.Price("gpt-4o-2024-05-13")?.Input);
        Assert.Equal(2.5, AgentPricing.Price("gpt-4o-2024-08-06")?.Input);
    }

    [Fact]
    public void PremiumsStackAndAggregatesNeverPayTheLongContextTier()
    {
        var writes = new AgentBillable(new AgentTokens(CacheWrite: 100_000));
        Assert.Equal(1.25, AgentPricing.Cost(writes, "gpt-6-astra").Cost!.Value, 6);
        Assert.Equal(0.5, AgentPricing.Cost(writes, "gpt-5.5").Cost!.Value, 6);
        // gpt-6-astra above 272K: input twice, output one and a half; the fast tier doubles both again.
        var longFast = new AgentBillable(new AgentTokens(300_000, Output: 1000), Fast: true);
        Assert.Equal((300_000 * 10 * 4 + 1000 * 50 * 3) / 1_000_000.0, AgentPricing.Cost(longFast, "gpt-6-astra").Cost!.Value, 9);
        Assert.Equal((300_000 * 10 * 2 + 1000 * 50 * 1.5) / 1_000_000.0,
                     AgentPricing.Cost(new AgentBillable(new AgentTokens(300_000, Output: 1000)), "gpt-6-astra").Cost!.Value, 9);
        Assert.Equal((300_000 * 10 + 1000 * 50) / 1_000_000.0,
                     AgentPricing.Cost(new AgentBillable(new AgentTokens(300_000, Output: 1000), IsAggregate: true), "gpt-6-astra").Cost!.Value, 9);
        Assert.Equal(2.72, AgentPricing.Cost(new AgentBillable(new AgentTokens(272_000)), "gpt-6-astra").Cost!.Value, 9);
        var plain = new AgentBillable(new AgentTokens(1_000_000));
        Assert.Equal(10, AgentPricing.Cost(plain with { Fast = true }, "claude-opus-5").Cost);
        Assert.Equal(5, AgentPricing.Cost(plain with { Fast = true }, "claude-opus-4-7").Cost);
        Assert.Equal(5.5, AgentPricing.Cost(plain with { Domestic = true }, "claude-opus-5").Cost!.Value, 4);
        var longPrompt = new AgentBillable(new AgentTokens(250_000, Output: 1000));
        Assert.Equal((250_000 * 6 + 1000 * 22.5) / 1_000_000, AgentPricing.Cost(longPrompt, "claude-sonnet-4-5").Cost!.Value, 9);
        Assert.Equal((250_000 * 3 + 1000 * 15) / 1_000_000.0, AgentPricing.Cost(longPrompt, "claude-sonnet-4-6").Cost!.Value, 9);
        Assert.Equal(0.03, AgentPricing.Cost(new AgentBillable(WebSearches: 3), "claude-opus-5").Cost!.Value, 9);
        Assert.Equal(0.01, AgentPricing.Cost(new AgentBillable(WebSearches: 1), "claude-opus-5").Cost!.Value, 9);
        Assert.Equal(0, AgentPricing.Cost(new AgentBillable(), "claude-3-5-sonnet").Cost);
    }

    [Fact]
    public void ModelNamesReadTheWayPeopleSayThem()
    {
        var expected = new Dictionary<string, string>
        {
            ["claude-opus-5-5"] = "Opus 5.5", ["claude-3-5-sonnet-20241022"] = "Sonnet 3.5", ["claude-sonnet-4-5-20250929"] = "Sonnet 4.5",
            ["gpt-6-astra"] = "GPT-6 Astra", ["gpt-5.1-codex-max"] = "GPT-5.1 Codex Max", ["gpt-6-astra-2026-10-01"] = "GPT-6 Astra",
            ["gpt-5-2025-08-07"] = "GPT-5", ["claude-mythos-preview"] = "Mythos Preview", ["claude-3-5-sonnet-latest"] = "Sonnet 3.5",
            ["gpt-daybreak-red-latest"] = "GPT Daybreak Red", ["gpt-7"] = "GPT-7", ["claude-opus-6"] = "Opus 6", ["gpt-7-codex"] = "GPT-7 Codex",
            ["relay/vendor/claude-opus-5-5"] = "Opus 5.5", ["relay/claude-opus-5.5"] = "Opus 5.5", ["stealth/ox-alpha"] = "ox-alpha",
            ["relay/claude-sonnet-5.1:deep"] = "Sonnet 5.1", ["q7"] = "q7", ["q7-mini"] = "q7-mini",
            ["gpt-astra:20b"] = "GPT Astra:20B", ["gpt-astra:120b"] = "GPT Astra:120B",
        };
        foreach (var (model, name) in expected) Assert.Equal(name, AgentPricing.DisplayName(model));
    }

    [Fact]
    public void PlansAreRecognizedAndAnUnknownOneKeepsItsName()
    {
        Assert.Equal(new AgentPlan("Ultra", 200), AgentPlans.Claude("claude_max", "default_claude_max_20x"));
        Assert.Equal(new AgentPlan("Ultra", 200), AgentPlans.Claude("claude_ultra", "default_claude_ultra"));
        Assert.Equal(new AgentPlan("Max", 100), AgentPlans.Claude("claude_max", "default_claude_max_5x"));
        Assert.Equal(20, AgentPlans.Claude("claude_pro", null)?.MonthlyPrice);
        Assert.Null(AgentPlans.Claude(null, null));
        Assert.Equal(new AgentPlan("Pro", null), AgentPlans.Codex("pro"));
        Assert.Equal(new AgentPlan("Business", null), AgentPlans.Codex("business"));
        Assert.Equal(new AgentPlan("Free", 0), AgentPlans.Codex("free"));
        Assert.Equal(new AgentPlan("Go", 8), AgentPlans.Codex("go"));
        Assert.Equal(new AgentPlan("Example", null), AgentPlans.Claude("claude_example", null));
    }

    private static byte[] PriceJson(string schema = "1", string updated = "2026-09-22",
                                    string claude = """{"id":"claude-opus-5","input":5,"output":25,"cacheRead":0.5,"cacheWrite":6.25,"cacheWriteLong":10}""",
                                    string codex = """{"id":"gpt-6-astra","input":10,"output":50,"cacheRead":1}""", string extra = "") =>
        Encoding.UTF8.GetBytes($$$"""{"schema":{{{schema}}},"updated":"{{{updated}}}"{{{extra}}},"claude":{"webSearch":0.01,"usOnlyMultiplier":1.1,"models":[{{{claude}}}],"plans":[{"match":"max_20x","name":"Max 20×","monthly":200}]},"codex":{"models":[{{{codex}}}],"plans":[{"match":"plus","name":"Plus","monthly":20}]}}""");

    [Fact]
    public void TheShippedListIsValidAndABrokenListIsRefusedWhole()
    {
        Assert.NotNull(Shipped);
        Assert.True(Shipped.Updated >= new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        Assert.True(Shipped.Claude.Count >= 23 && Shipped.Codex.Count >= 69 && Shipped.ClaudePlans.Count >= 6);
        var minimal = AgentPriceList.Decode(PriceJson(extra: ""","notes":"a field from a later list" """));
        Assert.Equal(new AgentPrice(10, 50, 1, 10, 10), minimal?.Codex[0].Price);
        Assert.Equal([new AgentPriceList.Plan("plus", "Plus", 20)], minimal!.CodexPlans);
        var longList = AgentPriceList.Decode(PriceJson(codex: """{"id":"gpt-6-astra","input":10,"output":50,"cacheRead":1,"longContext":{"above":272000,"inputMultiplier":2,"outputMultiplier":1.5}}"""));
        Assert.Equal(new AgentLongContext(272_000, 2, 1.5), longList?.Codex[0].Price.LongContext);
        var refused = new[]
        {
            PriceJson(schema: "2"), PriceJson(updated: "2026-13-40"), PriceJson(schema: "true"),
            PriceJson(claude: """{"id":"claude-opus-5","input":-5,"output":25,"cacheRead":0.5}"""),
            PriceJson(claude: """{"id":"claude-opus-5","input":true,"output":25,"cacheRead":0.5}"""),
            PriceJson(claude: """{"id":"gpt-6","input":5,"output":25,"cacheRead":0.5}"""),
            PriceJson(codex: """{"id":"claude-opus-5","input":5,"output":25,"cacheRead":0.5}"""),
            PriceJson(codex: """{"id":"GPT 6","input":5,"output":25,"cacheRead":0.5}"""),
            PriceJson(claude: """{"id":"claude-opus-5","input":5,"output":25,"cacheRead":0.5},{"id":"claude-opus-5","input":6,"output":30,"cacheRead":0.6}"""),
            PriceJson(claude: """{"id":"claude-opus-5","input":5,"output":25,"cacheRead":0.5,"fastMultiplier":50}"""),
            PriceJson(codex: """{"id":"gpt-6-astra","input":10,"output":50,"cacheRead":1,"longContext":{"above":5,"inputMultiplier":2,"outputMultiplier":1.5}}"""),
            PriceJson(codex: """{"id":"gpt-6-astra","input":10,"output":50,"cacheRead":1,"longContext":true}"""),
            Encoding.UTF8.GetBytes("not json"), Enumerable.Repeat((byte)0x20, AgentPriceList.MaximumSize + 1).ToArray(),
        };
        Assert.All(refused, data => Assert.Null(AgentPriceList.Decode(data)));
    }

    [Fact]
    public void TheNewerListWinsAndTheDownloadWinsATie()
    {
        var early = AgentPriceList.Decode(PriceJson(updated: "2026-09-22"));
        var late = AgentPriceList.Decode(PriceJson(updated: "2026-10-01"));
        var sameDay = AgentPriceList.Decode(PriceJson(updated: "2026-09-22", extra: ""","x":1"""))! with { WebSearch = 0.02 };
        Assert.Same(late, AgentPriceList.Newer(early, late));
        Assert.Same(late, AgentPriceList.Newer(late, early));
        Assert.Same(early, AgentPriceList.Newer(null, early));
        Assert.Same(early, AgentPriceList.Newer(early, null));
        Assert.Same(sameDay, AgentPriceList.Newer(early, sameDay));
        Assert.False(AgentPricing.Install(AgentPricing.List));
        Assert.True(AgentPriceSource.DownloadDue(U(200_000), U(100_000), U(100_000), false));
        Assert.False(AgentPriceSource.DownloadDue(U(200_000), U(100_000), U(190_000), true));
    }

    [Fact]
    public void ANewListRepricesEveryResponseAlreadyRead()
    {
        var store = new AgentUsageStore();
        store.Apply(Claude(ClaudeAssistant(), new AgentLogState(), Zero), "a", AgentProvider.Claude, false, Zero, Zero);
        var before = store.Records[0].Cost;
        var doubled = Shipped with
        {
            Updated = Shipped.Updated.AddDays(1),
            Claude = Shipped.Claude.Select(m => m.Id != "claude-opus-5-5" ? m : m with
            {
                Price = new AgentPrice(m.Price.Input * 2, m.Price.Output * 2, m.Price.CacheRead * 2, m.Price.CacheWrite * 2, m.Price.CacheWriteLong * 2),
            }).ToList(),
        };
        try
        {
            Assert.True(AgentPricing.Install(doubled));
            store.Reprice();
            Assert.Equal(2 * before!.Value, store.Records[0].Cost!.Value, 9);
        }
        finally
        {
            AgentPricing.Install(Shipped);
        }
        store.Reprice();
        Assert.Equal(before, store.Records[0].Cost);
    }

    // MARK: Claude Code logs

    [Fact]
    public void AClaudeReplyYieldsItsUsageKeyedByMessageAndRequest()
    {
        var entries = Claude(ClaudeAssistant(cwd: "/Users/me/code/app/.claude/worktrees/fix-1"), new AgentLogState(), Zero);
        var usage = FirstUsage(entries);
        Assert.Equal("claude:msg_1:req_1", usage.Key);
        Assert.Equal(("claude-opus-5-5", "s1", "app"), (usage.Record.Model, usage.Record.Session, usage.Record.Project));
        Assert.Equal(new AgentTokens(2, 17_218, 43_134, 225, 32), usage.Record.Tokens);
        Assert.Equal(17_218, usage.Billable.LongCacheWrite);
        Assert.Equal("app", AgentLogParser.ProjectName(@"C:\Users\me\code\app\.claude\worktrees\fix-1"));
        Assert.DoesNotContain(Claude(ClaudeAssistant(model: "<synthetic>"), new AgentLogState(), Zero), e => e is AgentLogEntry.Usage);
        Assert.Empty(Claude("""{"type":"summary","summary":"\"type\":\"assistant\""}""", new AgentLogState(), Zero));
    }

    [Fact]
    public void StreamedClaudeRepliesCountOnceAtTheirFinalOutput()
    {
        var store = new AgentUsageStore();
        foreach (var (output, file) in new[] { (8, "a"), (334, "a"), (334, "b") })
            store.Apply(Claude(ClaudeAssistant(output: output), new AgentLogState(), Zero), file, AgentProvider.Claude, false, Zero, Zero);
        Assert.Single(store.Records);
        Assert.Equal(334, store.Records[0].Tokens.Output);
        var expected = AgentPricing.Cost(new AgentBillable(new AgentTokens(2, 17_218, 43_134, 334, 32), LongCacheWrite: 17_218), "claude-opus-5-5").Cost;
        Assert.Equal(expected, store.Records[0].Cost);
        store.DropRecords(U(2_000_000_000));
        store.Apply(Claude(ClaudeAssistant(output: 400), new AgentLogState(), Zero), "a", AgentProvider.Claude, false, Zero, Zero);
        Assert.Single(store.Records);
        Assert.Equal(400, store.Records[0].Tokens.Output);
    }

    [Fact]
    public void ClaudeTurnsOpenWithAPromptAndEndWithTheReplyThatStops()
    {
        var now = T("2026-09-21T23:45:00.000Z");
        var state = new AgentLogState();
        var store = new AgentUsageStore { ReportsTransitions = true };
        IReadOnlyList<AgentUsageEvent> Feed(string line) => store.Apply(Claude(line, state, now), "main", AgentProvider.Claude, true, now, now);
        Assert.True(Feed(ClaudeUser(meta: true)).Count == 0 && store.Live.Count == 0);
        var side = new AgentLogState();
        Assert.Empty(Claude("""{"type":"user","isSidechain":true,"timestamp":"2026-09-21T23:41:00.000Z","sessionId":"s1","message":{"role":"user","content":"Explore the repo"}}""", side, now));
        Assert.False(side.TurnOpen);
        Feed(ClaudeUser(time: "2026-09-21T23:40:00.000Z"));
        Assert.Equal(T("2026-09-21T23:40:00.000Z"), Assert.Single(store.Live).Started);
        Feed(ClaudeAssistant(stop: "tool_use"));
        Assert.Equal([new AgentLogEntry.TurnActive(null)], Claude(ClaudeUser(toolResult: true), state, now));
        var quoted = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"if contains(line, \"[Request interrupted by user\") || contains(line, \"<local-command-stdout>\")"}]}}""";
        Assert.Equal([new AgentLogEntry.TurnActive(null)], Claude(quoted, state, now));
        Feed(ClaudeAssistant(id: "msg_2", request: "req_2", stop: "end_turn", sidechain: true));
        Assert.Single(store.Live);
        var finished = Assert.IsType<AgentUsageEvent.Finished>(Feed(ClaudeAssistant(id: "msg_3", request: "req_3", stop: "end_turn", time: "2026-09-21T23:44:30.000Z")).First());
        Assert.Equal((AgentProvider.Claude, 270.0, "app"), (finished.Provider, finished.Duration, finished.Project));
        Assert.True(finished.Cost > 0);
        Assert.Equal(3 * ReplyTokens.Total, finished.Tokens);
        Assert.Empty(store.Live);

        Feed(ClaudeUser());
        Assert.Empty(Feed("""{"type":"user","message":{"content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}"""));
        Assert.Empty(store.Live);
        Feed(ClaudeUser("<command-name>/model</command-name>"));
        Assert.Empty(store.Live);
        Feed("""{"type":"user","message":{"content":"<local-command-stdout>Set model</local-command-stdout>"}}""");
        Assert.Empty(store.Live);
        Feed(ClaudeUser("<command-name>/clear</command-name>"));
        Assert.Empty(store.Live);
        // A skill starts work at invocation, before its first model reply.
        var invoked = "2026-09-21T23:44:00.000Z";
        Feed(ClaudeUser("<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args><command-name>/clear</command-name></command-args>", invoked));
        Feed(ClaudeUser("Review the change", invoked, meta: true));
        Assert.Equal(T(invoked), store.Live[0].Started);
        var skill = Feed("""{"type":"assistant","timestamp":"2026-09-21T23:44:05.000Z","requestId":"req_skill","message":{"id":"msg_skill","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"<local-command-stdout>example</local-command-stdout>"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":3}}}""");
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.Claude, 5, store.Records[^1].Cost ?? 0, 5, "app")], skill);
        Assert.False(state.TurnOpen);
        // A tool that ends the turn finishes it without waiting for a reply.
        Feed(ClaudeUser());
        Feed(ClaudeAssistant(id: "msg_5", request: "req_5", time: "2026-09-21T23:40:10.000Z"));
        var structured = Feed("""{"type":"user","timestamp":"2026-09-21T23:40:15.000Z","sessionId":"s1","message":{"role":"user","content":[{"type":"tool_result","content":"Structured output provided successfully"}]},"toolUseResult":"Structured output provided successfully","toolEndsTurn":true}""");
        Assert.True(store.Live.Count == 0 && structured.Any(e => e is AgentUsageEvent.Finished));
        Assert.Empty(Feed("""{"type":"user","timestamp":"2026-09-21T23:40:16.000Z","message":{"content":[{"type":"tool_result","content":"ok"}]},"toolEndsTurn":true}"""));
        Feed(ClaudeUser());
        Feed(ClaudeAssistant(id: "msg_6", request: "req_6"));
        Feed("""{"type":"user","timestamp":"2026-09-21T23:43:00.000Z","message":{"content":[{"type":"tool_result","content":"\"toolEndsTurn\":true"}]},"toolEndsTurn":false}""");
        Feed("""{"type":"user","isSidechain":true,"timestamp":"2026-09-21T23:43:01.000Z","message":{"content":[{"type":"tool_result","content":"done"}]},"toolEndsTurn":true}""");
        Feed("""{"type":"user","timestamp":"2026-09-21T23:43:02.000Z","message":{"content":[{"type":"tool_result","content":"ok"}]},"toolUseResult":{"result":{"toolEndsTurn":true}}}""");
        Assert.Single(store.Live);
        Feed("""{"type":"user","message":{"content":[{"type":"text","text":"[Request interrupted by user]"}]}}""");
        Feed(ClaudeUser());
        // An error written in place of a reply ends the turn without a finish notice.
        Assert.Empty(Feed(ClaudeAssistant(id: "msg_4", request: "req_4", model: "<synthetic>", stop: "stop_sequence", time: "2026-09-21T23:44:40.000Z")));
        Assert.Empty(store.Live);
        // History read at start, and a turn found long after it ended, are not news.
        var quiet = new AgentUsageStore();
        var quietState = new AgentLogState();
        Assert.Empty(new[] { ClaudeUser(), ClaudeAssistant(stop: "end_turn") }.SelectMany(l => quiet.Apply(Claude(l, quietState, now), "main", AgentProvider.Claude, true, now, now)));
        var late = new AgentUsageStore { ReportsTransitions = true };
        var lateState = new AgentLogState();
        var found = new[] { ClaudeUser(), ClaudeAssistant(stop: "end_turn", time: "2026-09-21T23:44:30.000Z") }
            .SelectMany(l => late.Apply(Claude(l, lateState, now), "archived", AgentProvider.Claude, true, now, now.Plus(AgentUsageStore.LateEnd + 60)));
        Assert.Empty(found);
        Feed(ClaudeUser(time: "2026-09-21T23:50:00.000Z"));
        store.CloseIdleTurns(T("2026-09-22T00:05:00.000Z"), NotchAgentSupport.IdleTurn);
        Assert.Empty(store.Live);
    }

    [Fact]
    public void ClaudeTurnsEndOfflineAfterTheGraceUnlessTheModelRepliedOrAShellCommandRuns()
    {
        var now = T("2026-09-21T23:45:00.000Z");
        var drop = T("2026-09-21T23:43:00.000Z");
        var grace = AgentUsageStore.OfflineGrace;
        var state = new AgentLogState();
        var store = new AgentUsageStore { ReportsTransitions = true };
        IReadOnlyList<AgentUsageEvent> Feed(string line) => store.Apply(Claude(line, state, now), "main", AgentProvider.Claude, true, now, now);
        var codexState = new AgentLogState();
        IReadOnlyList<AgentUsageEvent> CodexLine(string line) => store.Apply(Codex(line, codexState, now), "rollout", AgentProvider.Codex, true, now, now);
        Feed(ClaudeUser());
        Feed(ClaudeAssistant(stop: "tool_use"));
        CodexLine("""{"timestamp":"2026-09-21T23:41:00.000Z","type":"event_msg","payload":{"type":"task_started"}}""");
        store.Apply([new AgentLogEntry.TurnBegan(now)], "events", AgentProvider.Copilot, true, now, now);
        store.Apply([new AgentLogEntry.TurnBegan(now)], "db#s1", AgentProvider.OpenCode, true, now, now);
        Assert.False(store.CloseOfflineTurns(drop, grace - 1));
        Assert.Equal(4, store.Live.Count);
        Assert.True(store.CloseOfflineTurns(drop, grace));
        Assert.Equal(Set(AgentProvider.Codex, AgentProvider.Copilot, AgentProvider.OpenCode), store.Live.Select(s => s.Provider).ToHashSet());
        Assert.True(FinishedBy(CodexLine("""{"timestamp":"2026-09-21T23:44:50.000Z","type":"event_msg","payload":{"type":"task_complete","duration_ms":230000}}"""), AgentProvider.Codex));
        Assert.Empty(Feed(ClaudeAssistant(id: "msg_2", request: "req_2", time: "2026-09-21T23:44:00.000Z")));
        var retried = store.Live.First(s => s.Provider == AgentProvider.Claude);
        Assert.Equal(T("2026-09-21T23:44:00.000Z"), retried.Started);
        Assert.Equal(ReplyTokens.Total, retried.Tokens.Total);
        Assert.True(retried.Cost > 0);
        Assert.False(store.CloseOfflineTurns(drop, grace + 60));
        Assert.Contains(store.Live, s => s.Provider == AgentProvider.Claude);
        store.CloseIdleTurns(now, 0);
        store.CloseOfflineTurns(T("2026-09-21T23:44:10.000Z"), grace);
        Feed(ClaudeAssistant(id: "msg_3", request: "req_3", time: "2026-09-21T23:44:30.000Z"));
        Assert.Equal(T("2026-09-21T23:44:30.000Z"), store.Live.First(s => s.Provider == AgentProvider.Claude).Started);

        static string Call(string tool, string id) => $$$"""{"type":"assistant","timestamp":"2026-09-21T23:44:40.000Z","sessionId":"s1","message":{"id":"msg_{{{id}}}","model":"<synthetic>","stop_reason":"tool_use","content":[{"type":"tool_use","id":"{{{id}}}","name":"{{{tool}}}","input":{}}]}}""";
        static string Result(string id) => $$$"""{"type":"user","sessionId":"s1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{id}}}","content":"ok"}]}}""";
        var late = T("2026-09-21T23:44:45.000Z");
        Feed(Call("WebFetch", "toolu_web"));
        Feed(Call("Bash", "toolu_sh"));
        Assert.Equal(["toolu_sh"], state.RunningCommands);
        Assert.False(store.CloseOfflineTurns(late, grace, new HashSet<string> { "main" }));
        Feed(Result("toolu_web"));
        Feed("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_web","content":"The pending command is toolu_sh"}]}}""");
        Feed("""{"type":"user","isSidechain":true,"message":{"content":[{"type":"tool_result","tool_use_id":"toolu_sh","content":"ok"}]}}""");
        Feed("""{"type":"user","isMeta":true,"message":{"content":[{"type":"tool_result","tool_use_id":"toolu_sh","content":"ok"}]}}""");
        Assert.Equal(["toolu_sh"], state.RunningCommands);
        Feed(Result("toolu_sh"));
        Assert.Empty(state.RunningCommands);
        Assert.True(store.CloseOfflineTurns(late, grace));
        Assert.DoesNotContain(store.Live, s => s.Provider == AgentProvider.Claude);
        Feed(Call("Bash", "toolu_left"));
        Feed("""{"type":"user","message":{"content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}""");
        Feed(ClaudeUser(time: "2026-09-21T23:44:50.000Z"));
        Assert.Empty(state.RunningCommands);
        // A session killed in the middle of a command, resumed: its next prompt forgets the command.
        var killed = new AgentLogState();
        foreach (var line in new[] { ClaudeUser(), Call("Bash", "toolu_killed"), ClaudeUser(meta: true),
                                     """{"type":"user","isSidechain":true,"sessionId":"s1","message":{"role":"user","content":"Explore the repo"}}""" })
            Claude(line, killed, now);
        Assert.Equal(["toolu_killed"], killed.RunningCommands);
        Claude(ClaudeUser("Go on", "2026-09-21T23:44:55.000Z"), killed, now);
        Assert.True(killed.TurnOpen && killed.RunningCommands.Count == 0);
        // A turn that reconnects after a short drop still reports its finish.
        var blip = new AgentUsageStore { ReportsTransitions = true };
        var blipState = new AgentLogState();
        IReadOnlyList<AgentUsageEvent> FeedBlip(string line) => blip.Apply(Claude(line, blipState, now), "blip", AgentProvider.Claude, true, now, now);
        FeedBlip(ClaudeUser());
        FeedBlip(ClaudeAssistant());
        blip.CloseOfflineTurns(drop, grace - 1);
        Assert.True(FinishedBy(FeedBlip(ClaudeAssistant(id: "msg_9", request: "req_9", stop: "end_turn", time: "2026-09-21T23:44:55.000Z")), AgentProvider.Claude));
    }

    // MARK: Codex logs

    private const string CodexMeta = """{"timestamp":"2026-09-22T14:44:23.705Z","type":"session_meta","payload":{"id":"s9","cwd":"/Users/me/code/web"}}""";
    private const string CodexContext = """{"timestamp":"2026-09-22T14:44:24.000Z","type":"turn_context","payload":{"model":"gpt-6-astra","cwd":"/Users/me/code/web"}}""";
    private const string CodexRecord = """{"timestamp":"2026-09-22T14:44:53.847Z","type":"token_usage_record","payload":{"session_id":"s9","response_id":"resp_1","usage":{"input_tokens":32167,"cached_input_tokens":20224,"cache_write_input_tokens":0,"output_tokens":156,"reasoning_output_tokens":7,"total_tokens":32323}}}""";

    [Fact]
    public void CodexRecordsSubtractCachedInputAndCarryLimitsAndTheMeasuredDuration()
    {
        var state = new AgentLogState();
        var store = new AgentUsageStore { ReportsTransitions = true };
        var lines = new[]
        {
            CodexMeta, CodexContext,
            """{"timestamp":"2026-09-22T14:44:25.000Z","type":"event_msg","payload":{"type":"task_started","started_at":1790088265}}""",
            CodexRecord,
            """{"timestamp":"2026-09-22T14:44:53.977Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":32167,"cached_input_tokens":20224,"output_tokens":156,"total_tokens":32323},"last_token_usage":{"input_tokens":32167,"cached_input_tokens":20224,"output_tokens":156,"total_tokens":32323}},"rate_limits":{"limit_id":"codex","primary":{"used_percent":72.0,"window_minutes":10080,"resets_at":1790390402},"secondary":null,"plan_type":"pro"}}}""",
            """{"timestamp":"2026-09-22T14:52:00.000Z","type":"event_msg","payload":{"type":"task_complete","completed_at":1790088720,"duration_ms":458431}}""",
        };
        var events = lines.SelectMany(l => store.Apply(Codex(l, state, Zero), "main", AgentProvider.Codex, true, Zero, U(1_790_088_730))).ToList();
        var usage = Assert.Single(store.Records);
        Assert.Equal(new AgentTokens(11_943, 0, 20_224, 156, 7), usage.Tokens);
        Assert.Equal(("gpt-6-astra", "web", "s9"), (usage.Model, usage.Project, usage.Session));
        Assert.Equal((11_943 * 10 + 20_224 * 1 + 156 * 50) / 1_000_000.0, usage.Cost!.Value, 9);
        var window = store.Limits[AgentProvider.Codex].Windows[0];
        Assert.Equal((AgentLimitKind.Weekly, 72.0, (int?)10_080, (DateTimeOffset?)U(1_790_390_402)), (window.Kind, window.UsedPercent, window.Minutes, window.ResetsAt));
        Assert.Equal("pro", store.CodexPlan);
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.Codex, 458.431, usage.Cost.Value, usage.Tokens.Total, "web")], events);

        // A model with an allowance of its own logs it under another id; only the main bucket is kept.
        var spark = Codex("""{"timestamp":"2026-09-22T14:53:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex_spark","limit_name":"GPT-5.3-Codex-Spark","primary":{"used_percent":3.0,"window_minutes":300,"resets_at":1790100000},"secondary":{"used_percent":1.0,"window_minutes":10080,"resets_at":1790390402},"plan_type":"pro"}}}""", new AgentLogState(), Zero);
        store.Apply(spark, "main", AgentProvider.Codex, true, Zero, Zero);
        Assert.DoesNotContain(spark, e => e is AgentLogEntry.Limits);
        Assert.Equal([72.0], store.Limits[AgentProvider.Codex].Windows.Select(w => w.UsedPercent));
        using (var empty = AgentLogParser.Object("{}")) Assert.True(AgentLogParser.IsMainBucket(empty!.RootElement));
        using (var main = AgentLogParser.Object("""{"limit_id":"codex"}""")) Assert.True(AgentLogParser.IsMainBucket(main!.RootElement));
        using (var other = AgentLogParser.Object("""{"limit_id":"codex_spark"}""")) Assert.False(AgentLogParser.IsMainBucket(other!.RootElement));

        // An archived session read later changes neither the plan nor the limits in use; a newer reading does.
        var archived = Codex("""{"timestamp":"2026-08-01T10:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":{"used_percent":5.0,"window_minutes":10080,"resets_at":1786000000},"secondary":null,"plan_type":"plus"}}}""", new AgentLogState(), Zero);
        store.Apply(archived, "archived", AgentProvider.Codex, true, Zero, Zero);
        Assert.Contains(new AgentLogEntry.Plan("plus", T("2026-08-01T10:00:00.000Z")), archived);
        Assert.Equal("pro", store.CodexPlan);
        Assert.Equal([72.0], store.Limits[AgentProvider.Codex].Windows.Select(w => w.UsedPercent));
        store.Apply([new AgentLogEntry.Plan("business", T("2026-09-22T15:00:00.000Z"))], "main", AgentProvider.Codex, true, Zero, Zero);
        Assert.Equal("business", store.CodexPlan);
    }

    [Fact]
    public void CodexFastTierLegacyTotalsAndBusinessAllowances()
    {
        var plainCost = (11_943 * 10 + 20_224 * 1 + 156 * 50) / 1_000_000.0;
        var fastState = new AgentLogState();
        var fastStore = new AgentUsageStore();
        foreach (var line in new[] { CodexMeta, CodexContext,
                     """{"timestamp":"2026-09-22T14:44:24.500Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"service_tier":"fast","model":"gpt-6-astra"}}}""",
                     CodexRecord })
            fastStore.Apply(Codex(line, fastState, Zero), "fast", AgentProvider.Codex, false, Zero, Zero);
        Assert.Equal(2 * plainCost, fastStore.Records[0].Cost!.Value, 9);
        Assert.True(AgentLogParser.FastTier("priority") && AgentLogParser.FastTier("Fast") && !AgentLogParser.FastTier("default") && !AgentLogParser.FastTier("flex"));

        // Older logs carry running totals only: the growth counts once, and the logged last response is preferred.
        var legacy = new AgentLogState();
        var legacyStore = new AgentUsageStore();
        static string Total(int input, int output, bool last) =>
            "{\"timestamp\":\"2026-09-22T15:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + "}" + (last ? ",\"last_token_usage\":{\"input_tokens\":" + (input - 100) + ",\"output_tokens\":" + (output - 10) + "}" : "") + "}}}";
        foreach (var line in new[] { Total(100, 10, false), Total(100, 10, false), Total(300, 40, false), Total(450, 60, true) })
            legacyStore.Apply(Codex(line, legacy, Zero), "old", AgentProvider.Codex, false, Zero, Zero);
        Assert.Equal([100L, 200, 350], legacyStore.Records.Select(r => r.Tokens.Input));
        Assert.Equal([10L, 30, 50], legacyStore.Records.Select(r => r.Tokens.Output));

        using (var slots = AgentLogParser.Object("""{"primary":{"used_percent":40.0,"window_minutes":10080,"resets_in_seconds":60},"secondary":{"used_percent":12.5,"window_minutes":300,"resets_at":1790000000}}"""))
        {
            var windows = AgentLogParser.CodexWindows(slots!.RootElement, U(1_000));
            Assert.Equal([AgentLimitKind.Session, AgentLimitKind.Weekly], windows.Select(w => w.Kind));
            Assert.Equal(U(1_060), windows[^1].ResetsAt);
        }
        var business = Codex("""{"timestamp":"2026-09-22T15:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":null,"secondary":null,"individual_limit":{"limit":"2500","used":"73.16","remaining_percent":97,"resets_at":1793491201},"spend_control_reached":null,"plan_type":"business"}}}""", new AgentLogState(), Zero)
            .OfType<AgentLogEntry.Limits>().First().Reading.Windows.Single();
        Assert.Equal(("codex.individual", 3.0, (DateTimeOffset?)U(1_793_491_201)), (business.Id, business.UsedPercent, business.ResetsAt));
        using (var both = AgentLogParser.Object("""{"primary":{"used_percent":40.0,"window_minutes":300},"individual_limit":{"remaining_percent":10}}"""))
            Assert.Equal(["codex.300"], AgentLogParser.CodexWindows(both!.RootElement, Zero).Select(w => w.Id));
        var aborted = new AgentLogState { TurnOpen = true };
        Assert.Equal([new AgentLogEntry.TurnEnded(T("2026-09-22T15:00:00.000Z"), false, 10.961)],
                     Codex("""{"timestamp":"2026-09-22T15:00:00.000Z","type":"event_msg","payload":{"type":"turn_aborted","duration_ms":10961}}""", aborted, Zero));
        var line2 = Encoding.UTF8.GetBytes("""{"timestamp":"2026-09-22T15:00:00.000Z","ordinal":3,"type":"event_msg","payload":{"type":"token_count","info":null}}""");
        var kind = AgentLogParser.FirstType(line2)!.Value;
        Assert.Equal("event_msg", kind.Name);
        Assert.Equal("token_count", AgentLogParser.FirstType(line2, kind.End)?.Name);
        // Records quoted inside another line's payload are not read as the line's own.
        var quoted = """{"type":"event_msg","payload":{"type":"task_complete","duration_ms":1}}""";
        var quiet = new AgentLogState { TurnOpen = true };
        Assert.Empty(Codex($$$"""{"timestamp":"2026-09-22T15:00:00.000Z","type":"compacted","payload":{"replacement_history":[{{{string.Join(",", Enumerable.Repeat(quoted, 2000))}}}]}}""", quiet, Zero));
        Assert.Empty(Codex($$$"""{"timestamp":"2026-09-22T15:00:00.000Z","type":"event_msg","payload":{"type":"item_completed","item":{{{quoted}}}}}""", quiet, Zero));
        Assert.True(quiet.TurnOpen && quiet.Session.Length == 0 && !quiet.SawUsageRecords);
    }

    // MARK: OpenCode rows

    [Fact]
    public void OpenCodeRowsReportUsageCostAndTurnsPerSession()
    {
        var state = new AgentLogState();
        var userEntries = OpenCode("""{"session_id":"s_oc_1","time_created":1790088000,"directory":"/Users/me/code/backend","role":"user","parts":[{"type":"text","text":"Implement feature"}]}""", state, Zero);
        Assert.Equal([new AgentLogEntry.TurnBegan(U(1_790_088_000))], userEntries);
        var activeEntries = OpenCode("""{"id":"msg_1","session_id":"s_oc_1","time_created":1790088005,"directory":"/Users/me/code/backend","role":"assistant","model_id":"stealth/ox-alpha","provider_id":"relay","cost":0.0042,"tokens":{"total":1250,"input":1000,"output":200,"reasoning":50,"cache":{"read":50,"write":0}},"finish":"tool-calls"}""", state, Zero);
        var usage = FirstUsage(activeEntries);
        Assert.Equal("opencode:s_oc_1:msg_1", usage.Key);
        Assert.Equal(("stealth/ox-alpha", "backend", "s_oc_1", (double?)0.0042), (usage.Record.Model, usage.Record.Project, usage.Record.Session, usage.Record.Cost));
        Assert.Equal(new AgentTokens(1000, 0, 50, 250, 50), usage.Record.Tokens);
        Assert.Contains(new AgentLogEntry.TurnActive(U(1_790_088_005)), activeEntries);
        var stopEntries = OpenCode("""{"id":"msg_2","session_id":"s_oc_1","time_created":1790088020,"directory":"/Users/me/code/backend","role":"assistant","model_id":"stealth/ox-alpha","provider_id":"relay","cost":0.0015,"tokens":{"total":500,"input":400,"output":100,"reasoning":0,"cache":{"read":0,"write":0}},"finish":"stop"}""", state, Zero);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_088_020), true, 20), stopEntries);
        var store = new AgentUsageStore { ReportsTransitions = true };
        var events = new[] { userEntries, activeEntries, stopEntries }
            .SelectMany(e => store.Apply(e, "opencode.db#s_oc_1", AgentProvider.OpenCode, true, Zero, U(1_790_088_020))).ToList();
        Assert.Equal(2, store.Records.Count);
        Assert.Equal(0.0057, store.Records.Sum(r => r.Cost ?? 0), 6);
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.OpenCode, 20, 0.0042 + 0.0015, 1800, "backend")], events);
        var beforeCost = store.Records[0].Cost;
        store.Reprice();
        Assert.Equal(beforeCost, store.Records[0].Cost);

        // A recorded zero for an unknown model is its cost; a listed model is priced from the list.
        var fallback = new AgentLogState();
        var unpriced = FirstUsage(OpenCode("""{"id":"msg_3","session_id":"s_oc_2","time_created":1790088030,"directory":"/Users/me/code/web","role":"assistant","model_id":"unknown-local-model","cost":0,"tokens":{"total":100,"input":80,"output":20,"reasoning":0,"cache":{"read":0,"write":0}},"finish":"stop"}""", fallback, Zero));
        Assert.True(unpriced.Record.Cost == 0 && !unpriced.Record.ReportedCost);
        var priced = FirstUsage(OpenCode("""{"id":"msg_4","session_id":"s_oc_3","time_created":1790088040,"directory":"/Users/me/code/web","role":"assistant","model_id":"gpt-6-astra","cost":0,"tokens":{"total":100,"input":80,"output":20,"reasoning":0,"cache":{"read":0,"write":0}},"finish":"stop"}""", fallback, Zero));
        Assert.Equal(0.0018, priced.Record.Cost!.Value, 6);
        // Reasoning joins the output: 300 + 200 = 500.
        var reasoning = FirstUsage(OpenCode("""{"id":"msg_rsn","session_id":"s_rsn","time_created":1790089500,"directory":"/Users/me/code/backend","role":"assistant","model_id":"gpt-6-astra","cost":0,"tokens":{"total":1500,"input":1000,"output":300,"reasoning":200,"cache":{"read":0,"write":0}},"finish":"stop"}""", new AgentLogState(), Zero)).Record;
        Assert.Equal((500L, 200L, 1500L), (reasoning.Tokens.Output, reasoning.Tokens.Reasoning, reasoning.Tokens.Total));
        Assert.Equal(0.035, reasoning.Cost!.Value, 6);
        var stringModel = FirstUsage(OpenCode("""{"id":"ast_str_m","session_id":"s_sm","time_created":1790089400,"directory":"/p","role":"assistant","model":"relay/claude-opus-5.5","tokens":{"input":10,"output":5}}""", new AgentLogState(), Zero));
        Assert.Equal("relay/claude-opus-5.5", stringModel.Record.Model);
    }

    [Fact]
    public void OpenCodeSessionsKeepTheirOwnTurnsAndIgnoreRepeatedRows()
    {
        var multi = new AgentLogState();
        Assert.Equal([new AgentLogEntry.TurnBegan(U(1_790_089_000))], OpenCode("""{"session_id":"s_A","time_created":1790089000,"directory":"/projA","role":"user"}""", multi, Zero));
        Assert.Equal([new AgentLogEntry.TurnBegan(U(1_790_089_005))], OpenCode("""{"session_id":"s_B","time_created":1790089005,"directory":"/projB","role":"user"}""", multi, Zero));
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_089_020), true, 20),
                        OpenCode("""{"id":"mA","session_id":"s_A","time_created":1790089020,"directory":"/projA","role":"assistant","cost":0.002,"tokens":{"total":100,"input":80,"output":20},"finish":"stop"}""", multi, Zero));
        Assert.True(multi.OpenCodeSessions["s_B"].TurnOpen);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_089_035), true, 30),
                        OpenCode("""{"id":"mB","session_id":"s_B","time_created":1790089035,"directory":"/projB","role":"assistant","cost":0.003,"tokens":{"total":200,"input":150,"output":50},"finish":"stop"}""", multi, Zero));
        Assert.False(multi.OpenCodeSessions["s_B"].TurnOpen);

        // A row updated in place keeps the reported cost it gains.
        var update = new AgentUsageStore();
        var u = new AgentLogState();
        update.Apply(OpenCode("""{"id":"m_up","session_id":"s_u","time_created":1790089100,"directory":"/p","role":"assistant","model_id":"stealth/ox-alpha","cost":0.002,"tokens":{"total":100,"input":80,"output":20}}""", u, Zero), "db#s_u", AgentProvider.OpenCode, true, Zero, Zero);
        Assert.Equal(0.002, update.Records[0].Cost);
        update.Apply(OpenCode("""{"id":"m_up","session_id":"s_u","time_created":1790089100,"time_updated":1790089105,"directory":"/p","role":"assistant","model_id":"stealth/ox-alpha","cost":0.0035,"tokens":{"total":200,"input":150,"output":50}}""", u, Zero), "db#s_u", AgentProvider.OpenCode, true, Zero, Zero);
        Assert.True(update.Records.Count == 1 && update.Records[0].Cost == 0.0035 && update.Records[0].Tokens.Total == 200);
        update.Apply(OpenCode("""{"id":"m_up","session_id":"s_u","time_created":1790089100,"time_updated":1790089110,"directory":"/p","role":"assistant","model_id":"stealth/ox-alpha","cost":0.004,"tokens":{"total":200,"input":150,"output":50},"finish":"stop"}""", u, Zero), "db#s_u", AgentProvider.OpenCode, true, Zero, Zero);
        Assert.True(update.Records.Count == 1 && update.Records[0].Cost == 0.004);

        // A prompt's summary saved after the reply finished is no new prompt.
        var post = new AgentLogState();
        OpenCode("""{"id":"usr_1","session_id":"s_pf","time_created":1790089200,"directory":"/p","role":"user"}""", post, Zero);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_089_205), true, 5),
                        OpenCode("""{"id":"ast_1","parentID":"usr_1","session_id":"s_pf","time_created":1790089205,"directory":"/p","role":"assistant","finish":"stop"}""", post, Zero));
        Assert.Empty(OpenCode("""{"id":"usr_1","session_id":"s_pf","time_created":1790089200,"time_updated":1790089206,"directory":"/p","role":"user","summary":{"diffs":[]}}""", post, Zero));
        Assert.False(post.OpenCodeSessions["s_pf"].TurnOpen);
        // An aborted reply ends the turn unfinished.
        var abort = new AgentLogState();
        OpenCode("""{"id":"usr_ab","session_id":"s_ab","time_created":1790089300,"directory":"/p","role":"user"}""", abort, Zero);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_089_305), false, 5),
                        OpenCode("""{"id":"ast_ab","parentID":"usr_ab","session_id":"s_ab","time_created":1790089305,"directory":"/p","role":"assistant","error":{"name":"MessageAbortedError","data":{"message":"Aborted"}}}""", abort, Zero));
        // A reply to an earlier prompt never closes the newer prompt's turn, nor does an update to a finished one.
        var o2 = new AgentLogState();
        OpenCode("""{"id":"usr_1","session_id":"s_o2","time_created":1790089700,"directory":"/p","role":"user"}""", o2, Zero);
        OpenCode("""{"id":"usr_2","session_id":"s_o2","time_created":1790089705,"directory":"/p","role":"user"}""", o2, Zero);
        Assert.Equal("usr_2", o2.OpenCodeSessions["s_o2"].ActiveUserMessageId);
        Assert.False(Ended(OpenCode("""{"id":"ast_1","parentID":"usr_1","session_id":"s_o2","time_created":1790089710,"directory":"/p","role":"assistant","finish":"stop","tokens":{"input":50,"output":25}}""", o2, Zero)));
        Assert.True(Ended(OpenCode("""{"id":"ast_2","parentID":"usr_2","session_id":"s_o2","time_created":1790089715,"directory":"/p","role":"assistant","finish":"stop","tokens":{"input":60,"output":30}}""", o2, Zero)));
        var duplicate = OpenCode("""{"id":"ast_2","parentID":"usr_2","session_id":"s_o2","time_created":1790089715,"time_updated":1790089720,"directory":"/p","role":"assistant","finish":"stop","tokens":{"input":70,"output":35}}""", o2, Zero);
        Assert.DoesNotContain(duplicate, e => e is AgentLogEntry.TurnBegan or AgentLogEntry.TurnEnded);
        // The completion time is the end, and the store reports it.
        var s17 = new AgentLogState();
        var store17 = new AgentUsageStore { ReportsTransitions = true };
        store17.Apply(OpenCode("""{"id":"u17","session_id":"s17","time_created":1790089400,"directory":"/p","role":"user"}""", s17, Zero), "db#s17", AgentProvider.OpenCode, true, Zero, Zero);
        var a17 = OpenCode("""{"id":"a17","parentID":"u17","session_id":"s17","time_created":1790089405,"time_updated":1790089425,"time":{"completed":1790089425000},"directory":"/p","role":"assistant","cost":0.01,"tokens":{"total":500,"input":400,"output":100},"finish":"stop"}""", s17, Zero);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_089_425), true, 25), a17);
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.OpenCode, 25, 0.01, 500, "p")],
                     store17.Apply(a17, "db#s17", AgentProvider.OpenCode, true, Zero, U(1_790_089_425)));
    }

    [Fact]
    public void OpenCodeRepricingKeepsReportedChargesAndPricesNewlyListedModels()
    {
        var store = new AgentUsageStore();
        var state = new AgentLogState();
        void Feed(string line) => store.Apply(OpenCode(line, state, Zero), "db#s_rp", AgentProvider.OpenCode, false, Zero, Zero);
        Feed("""{"id":"m_rep","session_id":"s_rp","time_created":1790089800,"directory":"/p","role":"assistant","model_id":"stealth/ox-alpha","cost":0.005,"tokens":{"input":1000,"output":200}}""");
        Feed("""{"id":"m_list","session_id":"s_rp","time_created":1790089810,"directory":"/p","role":"assistant","model_id":"claude-3-5-sonnet","cost":0,"tokens":{"input":1000,"output":200}}""");
        Feed("""{"id":"m_unp","session_id":"s_rp","time_created":1790089820,"directory":"/p","role":"assistant","model_id":"custom-nova-1","cost":0,"tokens":{"input":1000,"output":200}}""");
        AgentUsageRecord Of(string model) => store.Records.First(r => r.Model == model);
        Assert.True(Of("stealth/ox-alpha").ReportedCost && Of("stealth/ox-alpha").Cost == 0.005);
        Assert.False(Of("claude-3-5-sonnet").ReportedCost);
        Assert.Equal(0.006, Of("claude-3-5-sonnet").Cost!.Value, 6);
        Assert.True(!Of("custom-nova-1").ReportedCost && Of("custom-nova-1").Cost == 0);
        var previous = AgentPricing.List;
        var updated = previous with
        {
            Updated = previous.Updated.AddDays(1),
            Claude = [.. previous.Claude.Where(m => m.Id != "claude-3-5-sonnet"), new AgentPriceList.Model("claude-3-5-sonnet", new AgentPrice(6, 30, 0.6, 7.5, 12))],
            Codex = [.. previous.Codex, new AgentPriceList.Model("custom-nova-1", new AgentPrice(4, 20, 0.4, 4, 4))],
        };
        try
        {
            AgentPricing.Install(updated);
            store.Reprice();
            Assert.Equal(0.005, Of("stealth/ox-alpha").Cost);
            Assert.Equal(0.012, Of("claude-3-5-sonnet").Cost!.Value, 6);
            Assert.Equal(0.008, Of("custom-nova-1").Cost!.Value, 6);
            Feed("""{"id":"m_loc","session_id":"s_rp","time_created":1790089830,"role":"assistant","model_id":"custom-ember-1","cost":0,"tokens":{"input":10,"output":2}}""");
            store.Reprice();
            Assert.Equal(0, Of("custom-ember-1").Cost);
            var tokens = new AgentTokens(1000, Output: 200);
            // A list-derived estimate never replaces a reported charge; a reported one replaces an estimate.
            store.Apply([new AgentLogEntry.Usage("opencode:s_rp:m_rep", new AgentUsageRecord(AgentProvider.OpenCode, Zero, "stealth/ox-alpha", "p", "s_rp", tokens, 0.015, 0), new AgentBillable(tokens))],
                        "db#s_rp", AgentProvider.OpenCode, false, Zero, Zero);
            Assert.True(Of("stealth/ox-alpha").Cost == 0.005 && Of("stealth/ox-alpha").ReportedCost);
            store.Apply([new AgentLogEntry.Usage("opencode:s_rp:m_list", new AgentUsageRecord(AgentProvider.OpenCode, Zero, "claude-3-5-sonnet", "p", "s_rp", tokens, 0.012, 0, reportedCost: true), new AgentBillable(tokens))],
                        "db#s_rp", AgentProvider.OpenCode, false, Zero, Zero);
            Assert.True(Of("claude-3-5-sonnet").Cost == 0.012 && Of("claude-3-5-sonnet").ReportedCost);
            store.Apply([new AgentLogEntry.Usage("opencode:s_rp:m_rep", new AgentUsageRecord(AgentProvider.OpenCode, Zero, "stealth/ox-alpha", "p", "s_rp", tokens, 0.008, 0, reportedCost: true), new AgentBillable(tokens))],
                        "db#s_rp", AgentProvider.OpenCode, false, Zero, Zero);
            Assert.True(Of("stealth/ox-alpha").Cost == 0.008 && Of("stealth/ox-alpha").ReportedCost);
        }
        finally
        {
            AgentPricing.Install(previous);
        }
    }

    [Fact]
    public void OpenCodeSubagentsCountTowardTheirParentsTurn()
    {
        const string db = @"C:\Users\me\.local\share\opencode\opencode.db";
        var now = U(1_790_088_100);
        var state = new AgentLogState();
        var store = new AgentUsageStore { ReportsTransitions = true };
        var events = new List<AgentUsageEvent>();
        void Read(OpenCodeRow row)
        {
            var line = OpenCodeRows.Line(row, version2: false)!;
            var entries = new OpenCodeReader().Parse(line, state, now);
            if (entries.Count == 0) return;
            var route = AgentLogRouting.Route(AgentProvider.OpenCode, db, state);
            events.AddRange(store.Apply(entries, route.File, AgentProvider.OpenCode, route.TracksTurns, now, now, route.Parent));
        }
        static string Values(string role, string? parentId, string? model, string? tokens, double? cost, string? finish) =>
            new JsonArray(role, role, parentId, null, model, null, tokens is null ? null : JsonNode.Parse(tokens), cost, finish, null, null, null, null).ToJsonString();
        Read(new OpenCodeRow(1, "m_p1", "s_parent", 1_790_088_000_000, 1_790_088_000_000, 10, "/Users/me/code/proj", null,
                             Values("user", null, null, null, null, null), false, false, false, "null"));
        Assert.True(store.Turns.ContainsKey(db + "#s_parent"));
        Read(new OpenCodeRow(2, "m_c1", "s_child", 1_790_088_010_000, 1_790_088_010_000, 10, "/Users/me/code/sub_proj", "s_parent",
                             Values("assistant", null, "stealth/ox-alpha", """{"total":100,"input":80,"output":20}""", 0.002, "stop"), false, false, false, null));
        var parentTurn = store.Turns[db + "#s_parent"];
        Assert.Equal(100, parentTurn.Tokens.Total);
        Assert.Equal(0.002, parentTurn.Cost, 6);
        Assert.Empty(events);
        Read(new OpenCodeRow(3, "m_p2", "s_parent", 1_790_088_020_000, 1_790_088_020_000, 10, "/Users/me/code/proj", null,
                             Values("assistant", "m_p1", "stealth/ox-alpha", """{"total":200,"input":150,"output":50}""", 0.003, "stop"), false, false, false, null));
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.OpenCode, 20, 0.002 + 0.003, 300, "proj")], events);
        Assert.False(store.Turns.ContainsKey(db + "#s_parent"));
    }

    [Fact]
    public void OpenCodeRowsBecomeLinesAndAResetForgetsTheDatabasesTurns()
    {
        var values = new JsonArray("assistant", null, null, null, null, JsonNode.Parse("""{"id":"provider/model","providerID":"provider"}"""),
                                   JsonNode.Parse("""{"input":8,"output":3,"reasoning":2,"cache":{"read":4,"write":1}}"""), 0.01, null,
                                   JsonNode.Parse("""{"created":1790088005000,"completed":1790088006000}"""), "/Users/me/code/v2", null, null).ToJsonString();
        var row = new OpenCodeRow(1, "m_v2", "s_v2", 1_790_088_005_000, null, 100, "/Users/me/code/v2", null, values, false, false, false, null);
        var line = OpenCodeRows.Line(row, version2: true)!;
        var json = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(("assistant", 2, "/Users/me/code/v2"), ((string?)json["role"], (int)json["version"]!, (string?)json["path"]!["cwd"]));
        Assert.Equal(1_790_088_005_000, (long)json["time_updated"]!);
        var entries = new OpenCodeReader().Parse(line, new AgentLogState(), Zero);
        Assert.Equal(new AgentTokens(8, 1, 4, 5, 2), FirstUsage(entries).Record.Tokens);
        Assert.Equal("v2", FirstUsage(entries).Record.Project);
        // A step OpenCode 2 cut short, without finish, error or usage, neither ends nor starts a turn.
        var cut = new JsonArray("assistant", null, null, null, null, null, null, null, null, JsonNode.Parse("""{"completed":1790088006000}"""), null, null, null).ToJsonString();
        Assert.Null(OpenCodeRows.Line(row with { Values = cut }, version2: true));
        Assert.NotNull(OpenCodeRows.Line(row with { Values = cut }, version2: false));
        Assert.Null(OpenCodeRows.Line(row with { SessionId = null }, version2: false));
        Assert.Null(OpenCodeRows.Line(row with { Values = "not json" }, version2: false));
        Assert.True(JsonNode.Parse(OpenCodeRows.Line(row with { ErrorType = "object", ToolCalls = true }, false)!)!["error"]!.GetValue<bool>());
        Assert.Contains("FROM session_message m", OpenCodeRows.Select(true, false, true, 5));
        Assert.Contains("m.time_created >= 5 AND", OpenCodeRows.Select(true, false, true, 5));
        Assert.Contains("FROM message m", OpenCodeRows.Select(false, true, false));

        var store = new AgentUsageStore();
        var state = new AgentLogState();
        store.Apply(OpenCode("""{"id":"u1","session_id":"s1","time_created":1790090000,"role":"user"}""", state, Zero), "db#s1", AgentProvider.OpenCode, true, Zero, Zero);
        store.Apply(OpenCode("""{"id":"u2","session_id":"s2","time_created":1790090001,"role":"user"}""", state, Zero), "db#s2", AgentProvider.OpenCode, true, Zero, Zero);
        Assert.Equal(2, store.Live.Count);
        var reset = new OpenCodeReader().Parse(OpenCodeRows.ResetLine, state, Zero);
        Assert.Equal([new AgentLogEntry.Reset(Zero)], reset);
        Assert.Empty(state.OpenCodeSessions);
        store.Apply(reset, "db#s1", AgentProvider.OpenCode, true, Zero, Zero);
        Assert.Empty(store.Live);
    }

    [Fact]
    public void OpenCodeRepliesWithoutUsageAndEveryFinish()
    {
        var state = new AgentLogState();
        var store = new AgentUsageStore();
        var prompt = OpenCode("""{"id":"u_sh","session_id":"s_e","time_created":1790090000,"role":"user"}""", state, Zero);
        var started = OpenCode("""{"id":"a_sh","parentID":"u_sh","session_id":"s_e","time_created":1790090001,"role":"assistant","model_id":"claude-3-5-sonnet","cost":0,"tokens":{"input":0,"output":0,"reasoning":0,"cache":{"read":0,"write":0}},"time":{"created":1790090001000}}""", state, Zero);
        Assert.Equal([new AgentLogEntry.TurnActive(U(1_790_090_001))], started);
        var shell = OpenCode("""{"id":"a_sh","parentID":"u_sh","session_id":"s_e","time_created":1790090001,"role":"assistant","model_id":"claude-3-5-sonnet","cost":0,"tokens":{"input":0,"output":0,"reasoning":0,"cache":{"read":0,"write":0}},"time":{"created":1790090001000,"completed":1790090004000}}""", state, Zero);
        Assert.Equal([new AgentLogEntry.TurnEnded(U(1_790_090_004), true, 4)], shell);
        OpenCode("""{"id":"u_f","session_id":"s_e","time_created":1790090010,"role":"user"}""", state, Zero);
        var failed = OpenCode("""{"id":"a_f","parentID":"u_f","session_id":"s_e","time_created":1790090011,"role":"assistant","cost":0,"tokens":{"input":0,"output":0},"error":{"name":"APIError"},"time":{"completed":1790090012000}}""", state, Zero);
        Assert.Equal([new AgentLogEntry.TurnEnded(U(1_790_090_012), false, 2)], failed);
        foreach (var entries in new[] { prompt, started, shell, failed }) store.Apply(entries, "db#s_e", AgentProvider.OpenCode, true, Zero, Zero);
        Assert.Empty(store.Records);

        IReadOnlyList<AgentLogEntry> Turn(string finish, string extra = "")
        {
            var s = new AgentLogState();
            OpenCode("""{"id":"u","session_id":"s_f","time_created":1790090100,"role":"user"}""", s, Zero);
            return OpenCode($$$"""{"id":"a","parentID":"u","session_id":"s_f","time_created":1790090101,"role":"assistant","tokens":{"input":10,"output":5},"finish":"{{{finish}}}"{{{extra}}},"time":{"completed":1790090105000}}""", s, Zero);
        }
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_090_105), true, 5), Turn("length"));
        Assert.True(Ended(Turn("content-filter")) && Ended(Turn("other")) && Ended(Turn("stop")));
        Assert.False(Ended(Turn("tool-calls")) || Ended(Turn("unknown")));
        Assert.False(Ended(Turn("stop", ""","tool_calls":true""")));
    }

    [Fact]
    public void OpenCodeStoppedLoopsSettleAndQuietSessionsAreLetGo()
    {
        var state = new AgentLogState();
        var store = new AgentUsageStore { ReportsTransitions = true };
        var events = new List<AgentUsageEvent>();
        IReadOnlyList<AgentLogEntry> Feed(string json, double at)
        {
            var entries = OpenCode(json, state, Zero);
            events.AddRange(store.Apply(entries, "db#s_x", AgentProvider.OpenCode, true, Zero, U(at)));
            return entries;
        }
        Feed("""{"id":"u1","session_id":"s_x","time_created":1790091000,"role":"user"}""", 1_790_091_000);
        var rejected = Feed("""{"id":"a1","parentID":"u1","session_id":"s_x","time_created":1790091001,"role":"assistant","cost":0.05,"tokens":{"input":500,"output":50},"finish":"tool-calls","time":{"created":1790091001000,"completed":1790091005000}}""", 1_790_091_005);
        Assert.Contains(new AgentLogEntry.TurnSettled(U(1_790_091_005)), rejected);
        Assert.False(store.CloseSettledTurns(U(1_790_091_010)));
        Assert.Single(store.Live);
        Assert.True(store.CloseSettledTurns(U(1_790_091_040)));
        Assert.True(store.Live.Count == 0 && events.Count == 0);
        var prompt = Feed("""{"id":"u2","session_id":"s_x","time_created":1790091120,"role":"user"}""", 1_790_091_120);
        Assert.Equal([new AgentLogEntry.TurnEnded(U(1_790_091_005), false, null), new AgentLogEntry.TurnBegan(U(1_790_091_120))], prompt);
        var answer = Feed("""{"id":"a2","parentID":"u2","session_id":"s_x","time_created":1790091121,"role":"assistant","cost":0.01,"tokens":{"input":100,"output":10},"finish":"stop","time":{"created":1790091121000,"completed":1790091130000}}""", 1_790_091_130);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_091_130), true, 10), answer);
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.OpenCode, 10, 0.01, 110, "")], events);

        var own = new AgentLogState();
        OpenCode("""{"id":"u1","session_id":"s_y","time_created":1790091200,"role":"user"}""", own, Zero);
        OpenCode("""{"id":"a1","parentID":"u1","session_id":"s_y","time_created":1790091201,"role":"assistant","tokens":{"input":5},"finish":"tool-calls","time":{"completed":1790091205000}}""", own, Zero);
        Assert.Equal([new AgentLogEntry.TurnActive(U(1_790_091_206))], OpenCode("""{"id":"c1","session_id":"s_y","time_created":1790091206,"role":"user"}""", own, Zero));

        var killed = new AgentLogState();
        OpenCode("""{"id":"u1","session_id":"s_k","time_created":1790091300,"role":"user"}""", killed, Zero);
        OpenCode("""{"id":"a1","parentID":"u1","session_id":"s_k","time_created":1790091301,"role":"assistant","tokens":{"input":5}}""", killed, Zero);
        Assert.Equal([new AgentLogEntry.TurnActive(U(1_790_178_000))], OpenCode("""{"id":"u2","session_id":"s_k","time_created":1790178000,"role":"user"}""", killed, Zero));
        var restarted = OpenCode("""{"id":"a2","parentID":"u2","session_id":"s_k","time_created":1790178001,"role":"assistant","tokens":{"input":5},"finish":"stop","time":{"completed":1790178010000}}""", killed, Zero);
        Assert.Equal([new AgentLogEntry.TurnEnded(null, false, null), new AgentLogEntry.TurnBegan(U(1_790_178_000))], restarted.Take(2));
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_178_010), true, 10), restarted);

        var longState = new AgentLogState();
        var all = new[]
        {
            """{"id":"u1","session_id":"s_l","time_created":1790091400,"role":"user"}""",
            """{"id":"a1","parentID":"u1","session_id":"s_l","time_created":1790091401,"role":"assistant","tokens":{"input":5}}""",
            """{"id":"u2","session_id":"s_l","time_created":1790092300,"role":"user"}""",
            """{"id":"a1","parentID":"u1","session_id":"s_l","time_created":1790091401,"role":"assistant","tokens":{"input":9},"finish":"tool-calls","time":{"completed":1790092400000}}""",
            """{"id":"a2","parentID":"u2","session_id":"s_l","time_created":1790092401,"role":"assistant","tokens":{"input":5},"finish":"stop","time":{"completed":1790092410000}}""",
        }.SelectMany(l => OpenCode(l, longState, Zero)).ToList();
        Assert.Single(all.OfType<AgentLogEntry.TurnBegan>());
        Assert.Equal([new AgentLogEntry.TurnEnded(U(1_790_092_410), true, 1010)], all.OfType<AgentLogEntry.TurnEnded>());

        // Quiet sessions are let go as another one starts, unless a reply is still being written.
        var quiet = new AgentLogState();
        var quietStore = new AgentUsageStore { ReportsTransitions = true };
        var quietEvents = new List<AgentUsageEvent>();
        IReadOnlyList<AgentLogEntry> FeedQuiet(string json, double at)
        {
            var entries = OpenCode(json, quiet, Zero);
            quietEvents.AddRange(quietStore.Apply(entries, "db#" + quiet.Session, AgentProvider.OpenCode, true, Zero, U(at)));
            return entries;
        }
        FeedQuiet("""{"id":"u1","session_id":"s_d","time_created":1790092000,"directory":"/code/app","role":"user"}""", 1_790_092_000);
        FeedQuiet("""{"id":"a1","parentID":"u1","session_id":"s_d","time_created":1790092001,"directory":"/code/app","role":"assistant","tokens":{"input":5},"finish":"stop","time":{"completed":1790092005000}}""", 1_790_092_005);
        FeedQuiet("""{"id":"w1","session_id":"s_w","time_created":1790092010,"role":"user"}""", 1_790_092_010);
        FeedQuiet("""{"id":"wa","parentID":"w1","session_id":"s_w","time_created":1790092011,"role":"assistant","tokens":{"input":5},"time":{"created":1790092011000}}""", 1_790_092_011);
        FeedQuiet("""{"id":"n1","session_id":"s_n","time_created":1790093000,"role":"user"}""", 1_790_093_000);
        Assert.True(!quiet.OpenCodeSessions.ContainsKey("s_d") && quiet.OpenCodeSessions.ContainsKey("s_w") && quiet.OpenCodeSessions.ContainsKey("s_n"));
        var again = FeedQuiet("""{"id":"u2","session_id":"s_d","time_created":1790093100,"directory":"/code/app","role":"user"}""", 1_790_093_100);
        var reply = FeedQuiet("""{"id":"a2","parentID":"u2","session_id":"s_d","time_created":1790093101,"directory":"/code/app","role":"assistant","model_id":"stealth/ox-alpha","cost":0.01,"tokens":{"input":100,"output":10},"finish":"stop","time":{"completed":1790093110000}}""", 1_790_093_110);
        Assert.Equal([new AgentLogEntry.TurnBegan(U(1_790_093_100))], again);
        Assert.Contains(new AgentLogEntry.TurnEnded(U(1_790_093_110), true, 10), reply);
        Assert.Equal(2, quietEvents.Count);
        Assert.Equal(new AgentUsageEvent.Finished(AgentProvider.OpenCode, 10, 0.01, 110, "app"), quietEvents[^1]);
        Assert.Equal(2, quietStore.Records.Count(r => r.Session == "s_d"));
        var many = new AgentLogState();
        for (var index = 0; index < 200; index++)
        {
            var start = 1_790_100_000 + index * 700;
            OpenCode($$"""{"id":"u","session_id":"s{{index}}","time_created":{{start}},"role":"user"}""", many, Zero);
            OpenCode($$$"""{"id":"a","parentID":"u","session_id":"s{{{index}}}","time_created":{{{start + 1}}},"role":"assistant","tokens":{"input":5},"finish":"stop","time":{"completed":{{{(start + 5) * 1000L}}}}}""", many, Zero);
        }
        Assert.Single(many.OpenCodeSessions);
    }

    // MARK: Copilot

    private static string CopilotEvent(string type, string data, double at, string? agent = null, string? id = null) =>
        $$"""{"data":{{data}},"id":"{{id ?? type + at}}","timestamp":{{1_790_000_000 + at}},"type":"{{type}}"{{(agent is null ? "" : $",\"agentId\":\"{agent}\"")}}}""";

    [Fact]
    public void CopilotEventsOpenTurnsCountRequestsAndCheckpointTokens()
    {
        var state = new AgentLogState();
        var store = new AgentUsageStore { ReportsTransitions = true };
        var now = U(1_790_000_300);
        var reader = new CopilotReader();
        Assert.True(reader.ReadsFullHistoryFirst);
        IReadOnlyList<AgentUsageEvent> Feed(string line) => store.Apply(reader.Parse(line, state, now), "events.jsonl", AgentProvider.Copilot, true, now, now);
        Feed(CopilotEvent("session.start", """{"sessionId":"demo","selectedModel":"gpt-6-sol","context":{"gitRoot":"/tmp/demo"}}""", 0));
        Assert.Equal(("demo", "gpt-6-sol", "demo"), (state.Session, state.Model, state.Project));
        Feed(CopilotEvent("user.message", """{"content":"synthetic task"}""", 60));
        Assert.Equal(U(1_790_000_060), Assert.Single(store.Live).Started);
        Feed(CopilotEvent("assistant.message", """{"apiCallId":"call-1","messageId":"chunk-1","phase":"commentary","toolRequests":[{"name":"x"}]}""", 120));
        Feed(CopilotEvent("assistant.message", """{"apiCallId":"call-1","messageId":"chunk-2","phase":"response"}""", 150));
        Assert.Equal(1, store.Records.Sum(r => r.Requests));
        Assert.Equal(U(1_790_000_120), store.Records[0].Date);
        Assert.Equal(0, store.Records[0].Cost);
        // A subagent's message is activity, never the root's lifecycle.
        Feed(CopilotEvent("assistant.message", """{"apiCallId":"call-2","model":"claude-haiku-4-5"}""", 160, agent: "helper"));
        Assert.Equal(2, store.Records.Sum(r => r.Requests));
        Assert.Empty(reader.Parse(CopilotEvent("abort", "{}", 165, agent: "helper"), state, now));
        Assert.Single(store.Live);
        Assert.True(FinishedBy(Feed(CopilotEvent("assistant.turn_end", """{"turnId":"0"}""", 170)), AgentProvider.Copilot));
        Assert.Empty(store.Live);
        // The shutdown checkpoint reports session tokens by model, at base rates.
        Feed(CopilotEvent("session.shutdown", """{"modelMetrics":{"gpt-6-sol":{"requests":{"count":1},"usage":{"inputTokens":1000,"outputTokens":200}}}}""", 200, id: "shutdown-1"));
        var checkpoint = store.Records.Single(r => r.Tokens.Total > 0);
        Assert.Equal(new AgentTokens(1000, Output: 200), checkpoint.Tokens);
        Assert.Equal(0, checkpoint.Requests);
        Assert.Equal(AgentPricing.Cost(new AgentBillable(new AgentTokens(1000, Output: 200), IsAggregate: true), "gpt-6-sol").Cost, checkpoint.Cost);
        // An envelope that repeats its own key is refused.
        Assert.Empty(reader.Parse("""{"type":"user.message","type":"abort","data":{}}""", new AgentLogState(), now));
        Assert.True(AgentLogParser.CopilotHistoryLine(Encoding.UTF8.GetBytes(CopilotEvent("abort", "{}", 1))));
        Assert.False(AgentLogParser.CopilotHistoryLine(Encoding.UTF8.GetBytes("""{"type":"tool.execution","data":{}}""")));
    }

    // MARK: Timestamps and lines

    [Fact]
    public void TimestampsParseWithoutAFormatterAndRefuseAnythingElse()
    {
        Assert.Equal(1_790_034_165.078, AgentTime.ToUnixSeconds(T("2026-09-21T23:42:45.078Z")), 5);
        Assert.Equal(1_790_114_400.123456, AgentTime.ToUnixSeconds(T("2026-09-22T22:00:00.123456+00:00")), 5);
        Assert.Equal(T("2026-09-22T22:00:00Z"), T("2026-09-22T19:00:00-03:00"));
        Assert.Null(AgentTimestamp.Parse("yesterday"));
        Assert.Null(AgentTimestamp.Parse("2026-13-01T00:00:00Z"));
    }

    [Fact]
    public void LinesAreSplitAcrossChunksAndOversizedOnesAreSkipped()
    {
        var splitter = new AgentLineSplitter(maximumLine: 16);
        var lines = new List<string>();
        void Feed(string text) => splitter.Feed(Encoding.UTF8.GetBytes(text), l => lines.Add(Encoding.UTF8.GetString(l.Span)));
        Feed("{\"a\":1}\n{\"b\"");
        Assert.Equal(["{\"a\":1}"], lines);
        Assert.Equal(4, splitter.PendingCount);
        Feed(":2}\n\n");
        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], lines);
        Feed("0123456789abcdefXYZ");
        Assert.True(splitter.Discarding);
        Feed("more\n{\"c\":3}\n");
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}"], lines);
        splitter.Feed("{\"d\":4}\nnope\n"u8, l => lines.Add(Encoding.UTF8.GetString(l.Span)), l => l.Span[0] == (byte)'{');
        Assert.Equal("{\"d\":4}", lines[^1]);
    }

    // MARK: Summaries

    private static AgentUsageRecord Record(AgentProvider provider, DateTimeOffset date, double? cost, long tokens = 1000, string model = "claude-opus-5",
                                           string project = "app") =>
        new(provider, date, model, project, "s", new AgentTokens(tokens), cost, 0);

    [Fact]
    public void SummariesAddWhatFallsInsideEachPeriodBucketAndWindow()
    {
        var utc = TimeZoneInfo.Utc;
        var now = T("2026-09-22T16:40:00Z");
        var records = new[]
        {
            Record(AgentProvider.Claude, T("2026-09-22T10:12:00Z"), 2),
            Record(AgentProvider.Claude, T("2026-09-22T11:30:00Z"), 5, model: "claude-sonnet-5"),
            Record(AgentProvider.Claude, T("2026-09-22T15:20:00Z"), 1),
            Record(AgentProvider.Codex, T("2026-09-22T16:30:00Z"), 4, model: "gpt-6-astra", project: "web"),
            Record(AgentProvider.Codex, T("2026-09-18T09:00:00Z"), 10, model: "gpt-6-astra", project: "web"),
            Record(AgentProvider.Claude, T("2026-09-01T09:00:00Z"), 100),
            Record(AgentProvider.Claude, T("2026-01-01T09:00:00Z"), 1000),
        };
        var both = Set(AgentProvider.Claude, AgentProvider.Codex);
        var snapshot = AgentUsageSummary.Snapshot(records, NoLimits, [], NoPlans, both, now, utc);
        Assert.Equal((12.0, 22.0, 122.0), (snapshot.Usage(AgentPeriod.Today).Total.Cost, snapshot.Usage(AgentPeriod.Week).Total.Cost, snapshot.Usage(AgentPeriod.Month).Total.Cost));
        Assert.Equal(AgentUsageSnapshot.DayCount, snapshot.Days.Count);
        Assert.Equal(AgentCalendar.StartOfDay(now, utc), snapshot.Days[^1].Start);
        Assert.Equal(122, snapshot.Days.Sum(d => d.Total.Cost));
        Assert.Equal(24, snapshot.Hours.Count);
        Assert.Equal((2.0, 4.0), (snapshot.Hours[10].Total.Cost, snapshot.Hours[16].Total.Cost));
        Assert.Equal(["Sonnet 5", "GPT-6 Astra", "Opus 5"], snapshot.Usage(AgentPeriod.Today).Models.Select(m => m.Name));
        Assert.Equal(["app", "web"], snapshot.Usage(AgentPeriod.Today).Projects.Select(m => m.Name));
        var totals = new AgentTotals();
        totals.Add(records[2]);
        Assert.Equal(new AgentBlock(T("2026-09-22T15:20:00Z"), T("2026-09-22T20:20:00Z"), totals), snapshot.ClaudeBlock);
        Assert.Equal(8, snapshot.BurnRate[AgentProvider.Codex].Cost);
        Assert.False(snapshot.BurnRate.ContainsKey(AgentProvider.Claude));
        Assert.Equal(records[3].Date, snapshot.LastActivity[AgentProvider.Codex]);
        Assert.True(snapshot.Seen.SetEquals(both));
        var unpriced = AgentUsageSummary.Snapshot([Record(AgentProvider.Codex, now, null, 500), records[0]], NoLimits, [], NoPlans, both, now, utc);
        Assert.False(unpriced.Usage(AgentPeriod.Today).FullyPriced);
        Assert.Equal(1, unpriced.Usage(AgentPeriod.Today).Total.Unpriced);
        Assert.Equal("Opus 5", unpriced.Usage(AgentPeriod.Today).Models[0].Name);
        Assert.Equal("≥ $2.00", AgentFormat.MinimumCost(unpriced.Usage(AgentPeriod.Today).Total.Cost, unpriced.Usage(AgentPeriod.Today).Total.Unpriced, English));
        var claudeOnly = AgentUsageSummary.Snapshot(records, NoLimits, [], NoPlans, Set(AgentProvider.Claude), now, utc);
        Assert.Equal(8, claudeOnly.Usage(AgentPeriod.Today).Total.Cost);
        Assert.DoesNotContain(AgentProvider.Codex, claudeOnly.Seen);
        Assert.Null(AgentUsageSummary.CurrentBlock(records.Take(3).ToList(), T("2026-09-22T20:20:00Z")));
    }

    [Fact]
    public void ClaudesWindowChainKeepsTheMinuteItsFirstRequestPlaced()
    {
        var morning = T("2026-09-22T06:10:00Z");
        var steady = Enumerable.Range(0, 35).Select(step => Record(AgentProvider.Claude, morning.AddSeconds(step * 1200), 1)).ToList();
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var day = AgentUsageSummary.Snapshot(steady, NoLimits, [], NoPlans, Set(AgentProvider.Claude), T("2026-09-22T17:30:00Z"), kolkata);
        Assert.Equal(T("2026-09-22T16:10:00Z"), day.ClaudeBlock?.Start);
        Assert.Equal(T("2026-09-22T21:10:00Z"), day.ClaudeBlock?.End);
    }

    [Fact]
    public void ASnapshotMovesWithTheClockOnlyWhenTimeAloneChangesIt()
    {
        var utc = TimeZoneInfo.Utc;
        var noon = T("2026-09-22T12:00:00Z");
        var hourOn = noon.AddHours(1);
        var nextDay = T("2026-09-23T00:00:30Z");
        var both = Set(AgentProvider.Claude, AgentProvider.Codex);
        var resting = AgentUsageSummary.Snapshot([Record(AgentProvider.Codex, noon.AddSeconds(-7200), 1)], NoLimits, [], NoPlans, both, noon, utc);
        Assert.False(AgentUsageSummary.MovesWithClock(resting, hourOn, utc));
        Assert.True(AgentUsageSummary.MovesWithClock(resting, nextDay, utc));
        var burning = AgentUsageSummary.Snapshot([Record(AgentProvider.Codex, noon.AddSeconds(-600), 1)], NoLimits, [], NoPlans, both, noon, utc);
        var windowOpen = AgentUsageSummary.Snapshot([Record(AgentProvider.Claude, noon.AddSeconds(-5400), 1)], NoLimits, [], NoPlans, both, noon, utc);
        Assert.True(AgentUsageSummary.MovesWithClock(burning, hourOn, utc));
        Assert.True(windowOpen.BurnRate.Count == 0 && windowOpen.ClaudeBlock is not null);
        Assert.True(AgentUsageSummary.MovesWithClock(windowOpen, hourOn, utc));
        Assert.True(AgentUsageSummary.MovesWithClock(new AgentUsageSnapshot(), noon, utc));
        var now = T("2026-09-22T16:40:00Z");
        Assert.Equal(1, AgentUsageSummary.Index(now, [now.AddSeconds(-10), now, now.AddSeconds(10)]));
        Assert.Null(AgentUsageSummary.Index(now.AddSeconds(-20), [now]));
    }

    [Fact]
    public void TheActivityCardCountsStreaksActiveDaysAndQuartiles()
    {
        var utc = TimeZoneInfo.Utc;
        var now = T("2026-09-23T12:00:00Z"); // a Wednesday
        var records = new[] { 0, 1, 2, 5 }.Select(back => Record(AgentProvider.Codex, now.AddDays(-back), back + 1.0)).ToList();
        var snapshot = AgentUsageSummary.Snapshot(records, NoLimits, [], NoPlans, Set(AgentProvider.Codex), now, utc);
        Assert.Equal(3, AgentActivity.Streak(snapshot.Days));
        Assert.Equal(4, AgentActivity.ActiveDays(snapshot.Days));
        Assert.True(AgentActivity.ByCost(snapshot.Days));
        Assert.Equal(AgentCalendar.StartOfDay(now.AddDays(-5), utc), AgentActivity.BusiestDay(snapshot.Days, true)?.Start);
        // Nothing yet today: the streak still counts up to yesterday.
        var yesterday = AgentUsageSummary.Snapshot(records.Skip(1).ToList(), NoLimits, [], NoPlans, Set(AgentProvider.Codex), now, utc);
        Assert.Equal(2, AgentActivity.Streak(yesterday.Days));
        var columns = AgentActivity.Columns(snapshot.Days, utc, DayOfWeek.Sunday);
        Assert.Equal(14, columns.Count);
        Assert.Equal(snapshot.Days[^1], columns[^1][3]);
        Assert.Equal(snapshot.Days.Count, columns.Sum(c => c.Count(d => d is not null)));
        var thresholds = AgentActivity.Thresholds(snapshot.Days, true);
        Assert.Equal([2.0, 3, 6], thresholds);
        Assert.Equal([0, 1, 2, 3, 4], new[] { 0.0, 1, 3, 4, 6.5 }.Select(v => AgentActivity.Level(v, thresholds)));
    }

    // MARK: Limits

    [Fact]
    public void LimitsPaceBindAndWarnOncePerCrossing()
    {
        var now = U(1_000_000);
        const int week = 10_080;
        AgentLimitWindow Window(double used, double resetsIn, string id = "w", int minutes = week) =>
            new(id, AgentLimitKind.Weekly, minutes, null, used, now.Plus(resetsIn));
        var half = 3.5 * 86_400;
        Assert.Equal(0.5, AgentLimitSupport.Pace(Window(75, half), now)?.Elapsed);
        Assert.Equal(60.0 / (7 * 86_400), AgentLimitSupport.Pace(Window(25, 7 * 86_400 - 60), now)!.Value.Elapsed, 12);
        Assert.Null(AgentLimitSupport.Pace(Window(25, -1), now));
        Assert.Null(AgentLimitSupport.Pace(Window(25, 8 * 86_400), now));
        var renewed = AgentLimitSupport.Current(Window(90, -1), now);
        Assert.True(renewed.UsedPercent == 0 && renewed.ResetsAt is null);
        var reading = new AgentLimits(AgentProvider.Codex, [Window(40, 100, "a"), Window(60, 50, "b")], now, AgentLimitSource.SessionLog);
        Assert.Equal("b", AgentLimitSupport.Binding(reading, now)?.Id);
        AgentLimits Limits(double used, double resets, double observed = 0) =>
            new(AgentProvider.Codex, [Window(used, resets)], now.Plus(observed), AgentLimitSource.SessionLog);
        Assert.Single(AgentLimitSupport.Crossings(Limits(70, 100), Limits(85, 100), 80));
        Assert.Empty(AgentLimitSupport.Crossings(Limits(85, 100), Limits(90, 100), 80));
        Assert.Single(AgentLimitSupport.Crossings(Limits(85, 100), Limits(85, 9000, 200), 80));
        Assert.Empty(AgentLimitSupport.Crossings(null, Limits(99, 100), 80));
        Assert.Empty(AgentLimitSupport.Crossings(Limits(85, 100), Limits(90, 520, 50), 80));
        Assert.Single(AgentLimitSupport.Crossings(Limits(95, 100), Limits(85, 100, 50), 80));
    }

    [Fact]
    public void AlertsWarnOnceRenewAndReportTheBudgetOncePerDay()
    {
        var utc = TimeZoneInfo.Utc;
        var now = T("2026-09-22T12:00:00Z");
        AgentLimits Reading(double used, double observed, double resets = 3600) =>
            new(AgentProvider.Codex, [new AgentLimitWindow("codex.300", AgentLimitKind.Session, 300, null, used, now.Plus(resets))], now.Plus(observed), AgentLimitSource.SessionLog);
        var alerts = new AgentAlerts();
        var records = new[] { Record(AgentProvider.Codex, now.AddHours(-1), 30) };
        Assert.Empty(alerts.CheckLimits(new Dictionary<AgentProvider, AgentLimits> { [AgentProvider.Codex] = Reading(90, 0) }, 80));
        alerts.Begin(new Dictionary<AgentProvider, AgentLimits> { [AgentProvider.Codex] = Reading(70, 0) }, records, 25, now, utc);
        var warning = Assert.IsType<AgentUsageEvent.LimitWarning>(Assert.Single(alerts.CheckLimits(
            new Dictionary<AgentProvider, AgentLimits> { [AgentProvider.Codex] = Reading(85, 60) }, 80)));
        Assert.Equal("codex.300", warning.Window.Id);
        Assert.Empty(alerts.CheckLimits(new Dictionary<AgentProvider, AgentLimits> { [AgentProvider.Codex] = Reading(90, 120) }, 80));
        // A banked reset renews the warned window before its time.
        Assert.IsType<AgentUsageEvent.LimitReset>(Assert.Single(alerts.CheckLimits(
            new Dictionary<AgentProvider, AgentLimits> { [AgentProvider.Codex] = Reading(5, 180) }, 80)));
        alerts.CheckLimits(new Dictionary<AgentProvider, AgentLimits> { [AgentProvider.Codex] = Reading(95, 240) }, 80);
        Assert.Empty(alerts.ReportRenewals(now.AddMinutes(30)));
        Assert.Single(alerts.ReportRenewals(now.AddHours(2)));
        // The budget was already passed at launch: not news today, news tomorrow.
        var today = AgentUsageSummary.Snapshot(records, NoLimits, [], NoPlans, Set(AgentProvider.Codex), now, utc);
        Assert.Null(alerts.CheckBudget(today, 25, utc));
        var tomorrow = AgentUsageSummary.Snapshot([Record(AgentProvider.Codex, now.AddDays(1), 26)], NoLimits, [], NoPlans, Set(AgentProvider.Codex), now.AddDays(1), utc);
        Assert.Equal(new AgentUsageEvent.BudgetReached(26, 25), alerts.CheckBudget(tomorrow, 25, utc));
        Assert.Null(alerts.CheckBudget(tomorrow, 25, utc));
        var finish = new AgentUsageEvent.Finished(AgentProvider.Codex, 45, 1, 10, "app");
        Assert.False(AgentAlerts.Delivers(finish, Set(AgentProvider.Codex), 60, 80, null));
        Assert.True(AgentAlerts.Delivers(finish, Set(AgentProvider.Codex), 30, 80, null));
        Assert.False(AgentAlerts.Delivers(finish, Set(AgentProvider.Claude), 30, 80, null));
        Assert.False(AgentAlerts.Delivers(warning, Set(AgentProvider.Codex), 30, null, null));
        Assert.True(AgentAlerts.Delivers(new AgentUsageEvent.BudgetReached(26, 25), Set(), null, null, 25));
    }

    // MARK: Live turns

    [Fact]
    public void LiveTurnsSurviveRereadsWaitAsideAndEndByProcessRecords()
    {
        var start = U(3_000_000);
        var tokens = new AgentTokens(1000, 0, 9000, 200);
        var record = new AgentUsageRecord(AgentProvider.Codex, start.AddSeconds(10), "gpt-6-sol", "app", "s", tokens, 0.5, 0);
        AgentLogEntry[] entries = [new AgentLogEntry.TurnBegan(start), new AgentLogEntry.Usage("codex:r1", record, new AgentBillable(tokens))];
        var store = new AgentUsageStore();
        store.Apply(entries, "/logs/a.jsonl", AgentProvider.Codex, true, start, start);
        store.Apply(entries, "/logs/a.jsonl", AgentProvider.Codex, true, start, start);
        var turn = Assert.Single(store.Live);
        Assert.Equal(("gpt-6-sol", "app", 10_200L, start), (turn.Model, turn.Project, turn.Tokens.Total, turn.Started));
        store.Apply([new AgentLogEntry.TurnBegan(start.AddSeconds(60))], "/logs/a.jsonl", AgentProvider.Codex, true, start, start);
        Assert.Equal(0, store.Live[0].Tokens.Total);
        Assert.True(store.Forget("/logs/a.jsonl"));
        Assert.Empty(store.Live);
        Assert.False(store.Forget("/logs/a.jsonl"));

        var quiet = new AgentUsageStore { ReportsTransitions = true };
        const string log = "/logs/b.jsonl";
        quiet.Apply([new AgentLogEntry.TurnBegan(start)], log, AgentProvider.Codex, true, start, start);
        quiet.CloseIdleTurns(start.AddSeconds(1200), NotchAgentSupport.IdleTurn);
        Assert.True(quiet.Live.Count == 0 && quiet.Waiting[log].Started == start);
        var resumedAt = start.AddSeconds(1210);
        quiet.Apply([new AgentLogEntry.Usage("codex:r2", record with { Date = resumedAt }, new AgentBillable(tokens))], log, AgentProvider.Codex, true, resumedAt, resumedAt);
        Assert.True(quiet.Live[0].Started == start && quiet.Waiting.Count == 0);
        var endedAt = start.AddSeconds(1300);
        Assert.Equal([new AgentUsageEvent.Finished(AgentProvider.Codex, 1300, 0.5, tokens.Total, "app")],
                     quiet.Apply([new AgentLogEntry.TurnEnded(endedAt, true, null)], log, AgentProvider.Codex, true, endedAt, endedAt));
        quiet.Apply([new AgentLogEntry.TurnBegan(endedAt)], log, AgentProvider.Codex, true, endedAt, endedAt);
        quiet.CloseIdleTurns(endedAt.AddSeconds(1200), NotchAgentSupport.IdleTurn);
        var nextStart = endedAt.AddSeconds(1500);
        quiet.Apply([new AgentLogEntry.TurnBegan(nextStart)], log, AgentProvider.Codex, true, nextStart, nextStart);
        Assert.True(quiet.Waiting.Count == 0 && quiet.Live[0].Started == nextStart);
        quiet.CloseIdleTurns(nextStart.Plus(AgentUsageStore.ResumeWindow(AgentProvider.Codex)), NotchAgentSupport.IdleTurn);
        Assert.True(quiet.Live.Count == 0 && quiet.Waiting.Count == 0);
        quiet.Apply([new AgentLogEntry.TurnBegan(start)], "/logs/c.jsonl", AgentProvider.Claude, true, start, start);
        quiet.CloseIdleTurns(start.AddSeconds(1200), NotchAgentSupport.IdleTurn);
        quiet.CloseIdleTurns(start.Plus(AgentUsageStore.ResumeWindow(AgentProvider.Claude) + 1), NotchAgentSupport.IdleTurn);
        Assert.Empty(quiet.Waiting);
        Assert.True(AgentUsageStore.ResumeWindow(AgentProvider.Claude) == 3600 && AgentUsageStore.ResumeWindow(AgentProvider.Codex) == 6 * 3600);

        // A Claude subagent's spend and activity count toward the turn it works for.
        const string sessionLog = "/x/p/s1.jsonl";
        const string helper = "/x/p/s1/subagents/agent-a1.jsonl";
        Assert.Equal(sessionLog, AgentLogRouting.ClaudeParent(helper));
        Assert.Equal(@"C:\x\p\s1.jsonl", AgentLogRouting.ClaudeParent(@"C:\x\p\s1\subagents\agent-a1.jsonl"));
        var team = new AgentUsageStore();
        team.Apply([new AgentLogEntry.TurnBegan(start)], sessionLog, AgentProvider.Claude, true, start, start);
        team.Apply([new AgentLogEntry.Usage("claude:m1:r1", new AgentUsageRecord(AgentProvider.Claude, start.AddSeconds(5), "claude-opus-5-5", "app", "s1", tokens, 1, 0), new AgentBillable(tokens))],
                   sessionLog, AgentProvider.Claude, true, start, start);
        var delegatedAt = start.AddSeconds(30);
        var route = AgentLogRouting.Route(AgentProvider.Claude, helper, new AgentLogState());
        team.Apply([new AgentLogEntry.Usage("claude:m2:r2", new AgentUsageRecord(AgentProvider.Claude, delegatedAt, "claude-haiku-4-5", "tools", "s1", tokens, 0.25, 0), new AgentBillable(tokens))],
                   route.File, AgentProvider.Claude, route.TracksTurns, start, start, route.Parent);
        var served = Assert.Single(team.Live);
        Assert.Equal((1.25, delegatedAt, "claude-opus-5-5", "app"), (served.Cost, served.LastActivity, served.Model, served.Project));
    }

    [Fact]
    public void ClaudeProcessRecordsEndTurnsWhoseProcessQuitOrWasKilled()
    {
        var start = U(3_000_000);
        var records = new string?[]
        {
            """{"pid":100,"sessionId":"quit","cwd":"/p"}""", """{"pid":200,"sessionId":"killed"}""", """{"pid":300,"sessionId":"killed"}""",
            """{"pid":500,"sessionId":"contained","pidDomain":"linux:4f2ac19e:4026531836"}""",
        };
        var running = new HashSet<int> { 100, 300 };
        Assert.Equal(new AgentSessionRegistry { Running = new HashSet<string> { "quit", "killed" }, Listed = true },
                     AgentSessionRegistry.FromRecords(records, running.Contains, listed: true));
        var partial = AgentSessionRegistry.FromRecords([.. records, """{"pid":"""], pid => pid == 100, listed: true);
        Assert.True(partial.Running.SetEquals(["quit"]) && partial.Ended.SetEquals(["killed"]) && !partial.Complete);

        var store = new AgentUsageStore();
        foreach (var name in new[] { "quit", "killed", "other", "partial" })
            store.Apply([new AgentLogEntry.TurnBegan(start)], $"/p/{name}.jsonl", AgentProvider.Claude, true, start, start);
        store.Apply([new AgentLogEntry.TurnBegan(start)], "/p/quit-codex.jsonl", AgentProvider.Codex, true, start, start);
        store.CloseEndedTurns(new AgentSessionRegistry { Running = new HashSet<string> { "quit", "partial" } });
        Assert.Equal(5, store.Live.Count);
        store.CloseEndedTurns(new AgentSessionRegistry { Complete = false });
        Assert.Equal(5, store.Live.Count);
        Assert.True(store.CloseEndedTurns(new AgentSessionRegistry { Running = new HashSet<string> { "partial" }, Ended = new HashSet<string> { "killed" } }));
        Assert.Equal(new HashSet<string> { "/p/other.jsonl", "/p/partial.jsonl", "/p/quit-codex.jsonl" }, store.Live.Select(s => s.Id).ToHashSet());
        Assert.Empty(store.Waiting);
        store.CloseEndedTurns(new AgentSessionRegistry());
        Assert.Equal(2, store.Live.Count);
        Assert.DoesNotContain(store.Live, s => s.Id == "/p/partial.jsonl");

        var relaunched = new AgentUsageStore();
        foreach (var name in new[] { "gone", "alive" })
            relaunched.Apply([new AgentLogEntry.TurnBegan(start)], $"/p/{name}.jsonl", AgentProvider.Claude, true, start, start);
        var alive = new HashSet<string> { "alive" };
        relaunched.CloseEndedTurns(new AgentSessionRegistry { Running = alive }, atLaunch: true);
        Assert.Equal(2, relaunched.Live.Count);
        relaunched.CloseEndedTurns(new AgentSessionRegistry { Running = alive, Complete = false, Listed = true }, atLaunch: true);
        Assert.Equal(2, relaunched.Live.Count);
        relaunched.CloseEndedTurns(new AgentSessionRegistry { Running = alive, Listed = true });
        Assert.Equal(2, relaunched.Live.Count);
        Assert.True(relaunched.CloseEndedTurns(new AgentSessionRegistry { Running = alive, Listed = true }, atLaunch: true));
        Assert.Equal(["/p/alive.jsonl"], relaunched.Live.Select(s => s.Id));

        var copilot = new AgentUsageStore();
        copilot.Apply([new AgentLogEntry.TurnBegan(start)], "/p/session/events.jsonl", AgentProvider.Copilot, true, start, start);
        Assert.False(copilot.CloseEndedTurns(new AgentSessionRegistry { Ended = new HashSet<string> { "events" }, Listed = true }, atLaunch: true));
        Assert.False(copilot.ShowsClaudeTurn);
        Assert.Single(copilot.Live);
    }

    // MARK: Strip

    private static AgentLiveSession Session(AgentProvider provider, DateTimeOffset now, double startedAgo) =>
        new(provider.RawValue(), provider, now.Plus(-startedAgo), now, "claude-opus-5-5", "app", new AgentTokens(1200, Output: 300), 4.56);

    private static AgentUsageSnapshot StripSnapshot(DateTimeOffset now, IReadOnlyList<AgentLiveSession> live, Dictionary<AgentProvider, AgentLimits>? limits = null) =>
        AgentUsageSummary.Snapshot([], limits ?? [], live, NoPlans, Set(AgentProvider.Claude, AgentProvider.Codex), now, TimeZoneInfo.Utc);

    [Fact]
    public void TheStripShowsTheChosenReadingAndFallsBackToTheTime()
    {
        var now = U(2_000_000);
        Assert.True(NotchAgentReadout.Elapsed.AdvancesWithClock() && NotchAgentReadout.Limit.AdvancesWithClock()
                    && !NotchAgentReadout.Tokens.AdvancesWithClock() && !NotchAgentReadout.Cost.AdvancesWithClock());
        foreach (var provider in AgentProviders.All)
        {
            foreach (var focus in new[] { NotchAgentLimitFocus.MostUsed, NotchAgentLimitFocus.Session, NotchAgentLimitFocus.Weekly })
            {
                var empty = new AgentUsageSnapshot();
                var sample = NotchAgentSupport.StripSampleSnapshot(empty, provider, focus, now);
                var used = focus == NotchAgentLimitFocus.Session ? 0.32 : 0.57;
                Assert.Equal(AgentFormat.Percent(used), NotchAgentSupport.StripReading(sample, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used, focus, now));
                Assert.Equal(AgentFormat.Percent(1 - used), NotchAgentSupport.StripReading(sample, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Remaining, focus, now));
                Assert.True(empty.Live.Count == 0 && empty.Limits.Count == 0);
                Assert.Equal("12:34", NotchAgentSupport.StripReading(sample, NotchAgentReadout.Elapsed, NotchAgentLimitDisplay.Remaining, focus, now));
                Assert.Equal(AgentFormat.Cost(4.56), NotchAgentSupport.StripReading(sample, NotchAgentReadout.Cost, NotchAgentLimitDisplay.Remaining, focus, now));
                Assert.Equal(AgentFormat.Tokens(20_000), NotchAgentSupport.StripReading(sample, NotchAgentReadout.Tokens, NotchAgentLimitDisplay.Remaining, focus, now));
            }
        }
        var shortTurn = StripSnapshot(now, [Session(AgentProvider.Claude, now, 754)]);
        var longTurn = StripSnapshot(now, [Session(AgentProvider.Claude, now, 3723), Session(AgentProvider.Codex, now, 60)]);
        string Reading(AgentUsageSnapshot s, NotchAgentReadout r, NotchAgentLimitDisplay d = NotchAgentLimitDisplay.Remaining,
                       NotchAgentLimitFocus f = NotchAgentLimitFocus.MostUsed, double later = 0) => NotchAgentSupport.StripReading(s, r, d, f, now.Plus(later));
        Assert.Equal("12:34", Reading(shortTurn, NotchAgentReadout.Elapsed));
        Assert.Equal("1:02:03", Reading(longTurn, NotchAgentReadout.Elapsed));
        Assert.Equal(AgentFormat.Cost(4.56), Reading(shortTurn, NotchAgentReadout.Cost));
        Assert.Equal(AgentFormat.Tokens(600), Reading(longTurn, NotchAgentReadout.Tokens));
        foreach (var readout in new[] { NotchAgentReadout.Tokens, NotchAgentReadout.Cost })
        {
            Assert.Equal(Reading(shortTurn, readout), Reading(shortTurn, readout, later: 60));
            Assert.NotEqual(Reading(shortTurn, readout), Reading(longTurn, readout));
        }
        var week = new AgentLimitWindow("w", AgentLimitKind.Weekly, 10_080, null, 79, now.Plus(86_400));
        var limited = StripSnapshot(now, [Session(AgentProvider.Claude, now, 754)],
                                    new() { [AgentProvider.Claude] = new AgentLimits(AgentProvider.Claude, [week], now, AgentLimitSource.ClaudeApp) });
        Assert.Equal(AgentFormat.Percent(0.21), Reading(limited, NotchAgentReadout.Limit));
        Assert.Equal(AgentFormat.Percent(0.79), Reading(limited, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used));
        Assert.Equal("12:34", Reading(shortTurn, NotchAgentReadout.Limit));
        Assert.Equal(AgentFormat.Percent(0.21, English) + " left", NotchAgentSupport.LimitShare(week, NotchAgentLimitDisplay.Remaining, p => p + " left", p => p + " used", English));
        Assert.Equal("79% used", NotchAgentSupport.LimitShare(week, NotchAgentLimitDisplay.Used, p => p + " left", p => p + " used", English));
        Assert.Equal(limited, NotchAgentSupport.StripSampleSnapshot(limited, AgentProvider.Codex, NotchAgentLimitFocus.Weekly, now));
        var both = new AgentLimits(AgentProvider.Claude,
        [
            new AgentLimitWindow("s", AgentLimitKind.Session, 300, null, 22, now.Plus(3_600)), week,
            new AgentLimitWindow("o", AgentLimitKind.Weekly, 10_080, "Opus", 95, now.Plus(86_400)),
        ], now, AgentLimitSource.ClaudeApp);
        var working = StripSnapshot(now, [Session(AgentProvider.Claude, now, 754)], new() { [AgentProvider.Claude] = both });
        Assert.Equal(AgentFormat.Percent(0.95), Reading(working, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used));
        Assert.Equal(AgentFormat.Percent(0.22), Reading(working, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used, NotchAgentLimitFocus.Session));
        Assert.Equal(AgentFormat.Percent(0.79), Reading(working, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used, NotchAgentLimitFocus.Weekly));
        Assert.Equal(AgentFormat.Percent(0), Reading(working, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used, NotchAgentLimitFocus.Session, 3_601));
        Assert.Equal(AgentFormat.Percent(0.79), Reading(limited, NotchAgentReadout.Limit, NotchAgentLimitDisplay.Used, NotchAgentLimitFocus.Session));
        var codexWeek = new AgentLimits(AgentProvider.Codex, [new AgentLimitWindow("cw", AgentLimitKind.Weekly, 10_080, null, 70, now.Plus(86_400))], now, AgentLimitSource.SessionLog);
        var resting = StripSnapshot(now, [], new() { [AgentProvider.Claude] = both, [AgentProvider.Codex] = codexWeek });
        Assert.Equal((AgentProvider.Claude, 22.0), NotchAgentSupport.RestingLimit(resting, NotchAgentLimitFocus.Session, now) is { } a ? (a.Provider, a.Window.UsedPercent) : default);
        Assert.Equal((AgentProvider.Codex, 70.0), NotchAgentSupport.RestingLimit(StripSnapshot(now, [], new() { [AgentProvider.Codex] = codexWeek }), NotchAgentLimitFocus.Session, now) is { } b ? (b.Provider, b.Window.UsedPercent) : default);
        Assert.Equal((AgentProvider.Claude, 95.0), NotchAgentSupport.RestingLimit(resting, NotchAgentLimitFocus.MostUsed, now) is { } c ? (c.Provider, c.Window.UsedPercent) : default);
        Assert.Equal(AgentFormat.Percent(1), Reading(limited, NotchAgentReadout.Limit, later: 86_401));
    }

    [Fact]
    public void TheStripsWidthFollowsTheReadingsShapeWithinItsWings()
    {
        Assert.Equal(NotchAgentSupport.ReadingShape("12:34"), NotchAgentSupport.ReadingShape("59:59"));
        Assert.NotEqual(NotchAgentSupport.ReadingShape("9:59"), NotchAgentSupport.ReadingShape("10:00"));
        Assert.Equal("$0,00", NotchAgentSupport.ReadingShape("$4,56"));
        Assert.Equal(58, NotchAgentSupport.StripWing(57.2, 300));
        Assert.Equal(185 + 116, 185 + 2 * NotchAgentSupport.StripWing(57.2, 300));
        Assert.Equal(44, NotchAgentSupport.StripWing(12, 300));
        Assert.Equal(80, NotchAgentSupport.StripWing(300, 300));
        Assert.Equal(72, NotchAgentSupport.StripWing(72, 200));
        Assert.Equal(0, NotchAgentSupport.StripWing(57, 30));
        Assert.Equal(15, NotchAgentSupport.StripTextSize(32));
    }

    // MARK: Preferences and layout

    [Fact]
    public void PreferencesKeepToTheirChoicesAndRanges()
    {
        Assert.Equal([NotchAgentCard.Trend, NotchAgentCard.Spend, NotchAgentCard.Limits, NotchAgentCard.Live, NotchAgentCard.Models, NotchAgentCard.Resets],
                     NotchAgentSupport.Cards("trend,unknown,trend,spend", "activity,projects"));
        Assert.Equal(NotchAgentEnums.AllCards, NotchAgentSupport.OrderedCards(null));
        Assert.Equal([AgentProvider.Claude, AgentProvider.OpenCode, AgentProvider.Copilot, AgentProvider.Cursor],
                     NotchAgentSupport.Providers(p => p == AgentProvider.Codex ? false : null));
        Assert.Equal([AgentProvider.Claude], NotchAgentSupport.Providers(p => p == AgentProvider.Claude));
        Assert.Equal(NotchAgentLimitFocus.MostUsed, NotchAgentSupport.LimitFocus("unknown"));
        Assert.Equal(NotchAgentLimitFocus.Weekly, NotchAgentSupport.LimitFocus("weekly"));
        Assert.Equal(NotchAgentReadout.Limit, NotchAgentSupport.Readout("limit"));
        Assert.Equal(NotchAgentReadout.Elapsed, NotchAgentSupport.Readout(null));
        Assert.Equal(NotchAgentLimitDisplay.Used, NotchAgentSupport.LimitDisplay("used"));
        Assert.Equal(AgentPeriod.Week, AgentPeriods.Parse("week"));
        Assert.Equal(AgentPeriod.Today, AgentPeriods.Parse("year"));
        Assert.Null(NotchAgentSupport.FinishMinimum(false, 30));
        Assert.Equal(60, NotchAgentSupport.FinishMinimum(null, null));
        Assert.Equal(3600, NotchAgentSupport.FinishMinimum(true, 5000));
        Assert.Equal(0, NotchAgentSupport.FinishMinimum(true, -5));
        Assert.Equal(95, NotchAgentSupport.LimitThreshold(null, 95));
        Assert.Equal(1, NotchAgentSupport.LimitThreshold(true, 0));
        Assert.Null(NotchAgentSupport.LimitThreshold(false, 80));
        Assert.Null(NotchAgentSupport.DailyBudget(-4));
        Assert.Null(NotchAgentSupport.DailyBudget(0));
        Assert.Equal(25, NotchAgentSupport.DailyBudget(25));
        Assert.Equal([0.0, 30, 60, 120, 300], NotchAgentSupport.FinishMinimums);
        Assert.Equal([50.0, 75, 80, 90, 95], NotchAgentSupport.LimitThresholds);
        Assert.Equal([0.0, 5, 10, 25, 50, 100, 250], NotchAgentSupport.Budgets);
        Assert.Equal(600, NotchAgentSupport.IdleTurn);
        var none = new HashSet<NotchAgentCard>();
        var limitsHidden = new HashSet<NotchAgentCard> { NotchAgentCard.Limits };
        Assert.True(NotchAgentSupport.LimitsInUse(false, 80, true, NotchAgentReadout.Elapsed, false, none));
        Assert.False(NotchAgentSupport.LimitsInUse(true, null, true, NotchAgentReadout.Elapsed, false, limitsHidden));
        Assert.False(NotchAgentSupport.LimitsInUse(false, null, true, NotchAgentReadout.Elapsed, false, none));
        Assert.True(NotchAgentSupport.LimitsInUse(true, null, true, NotchAgentReadout.Elapsed, false, none));
        Assert.True(NotchAgentSupport.LimitsInUse(false, null, true, NotchAgentReadout.Limit, false, limitsHidden));
        Assert.False(NotchAgentSupport.LimitsInUse(false, null, false, NotchAgentReadout.Limit, false, limitsHidden));
        Assert.True(NotchAgentSupport.LimitsInUse(false, null, false, NotchAgentReadout.Limit, true, limitsHidden));
    }

    [Fact]
    public void CardsPairInReadingOrderAndOnlyAgentsWithAllowancesGetALimitsCard()
    {
        var limits = new NotchAgentTile(NotchAgentCard.Limits, AgentProvider.Claude);
        var codex = new NotchAgentTile(NotchAgentCard.Limits, AgentProvider.Codex);
        var spend = new NotchAgentTile(NotchAgentCard.Spend, null);
        var trend = new NotchAgentTile(NotchAgentCard.Trend, null);
        Assert.Equal([limits, codex, trend], NotchAgentSupport.Tiles([NotchAgentCard.Limits, NotchAgentCard.Trend], [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Copilot]));
        Assert.False(AgentProvider.Copilot.ReportsLimits());
        Assert.True(AgentProvider.Cursor.ReportsLimits() && !AgentProvider.Cursor.ReadsLogs());
        Assert.Equal([spend], NotchAgentSupport.Tiles([NotchAgentCard.Limits, NotchAgentCard.Resets, NotchAgentCard.Spend], [AgentProvider.Copilot]));
        Assert.Equal([new NotchAgentTile(NotchAgentCard.Resets, AgentProvider.Codex), spend],
                     NotchAgentSupport.Tiles([NotchAgentCard.Resets, NotchAgentCard.Spend], [AgentProvider.Codex], showsResets: true));
        Assert.Equal("limits.claude", limits.Id);
        var rows = NotchAgentSupport.Rows([limits, codex, spend, trend], 424);
        Assert.Equal(3, rows.Count);
        Assert.Equal([limits, codex], rows[0]);
        Assert.Equal([spend], rows[1]);
        Assert.Equal([[limits], [trend], [codex]], NotchAgentSupport.Rows([limits, trend, codex], 424).Select(r => r.ToList()));
        Assert.Equal([[limits], [codex]], NotchAgentSupport.Rows([limits, codex], 330).Select(r => r.ToList()));
        Assert.Equal([NotchAgentCard.Spend, NotchAgentCard.Trend, NotchAgentCard.Models, NotchAgentCard.Projects], NotchAgentEnums.AllCards.Where(c => c.FollowsPeriod()));
        Assert.Equal(NotchAgentSupport.CardHeight + NotchAgentSupport.Spacing + NotchAgentSupport.ChartHeight,
                     NotchAgentSupport.ContentHeight([[limits, codex], [trend]]));
        Assert.Equal(0, NotchAgentSupport.ContentHeight([]));
    }

    [Fact]
    public void NumbersShortenWithoutRoundingUpInTheRegionsFormat()
    {
        Assert.Equal("950", AgentFormat.Tokens(950, English));
        Assert.Equal("4.2K", AgentFormat.Tokens(4_280, English));
        Assert.Equal("48K", AgentFormat.Tokens(48_900, English));
        Assert.Equal("1.2M", AgentFormat.Tokens(1_234_567, English));
        Assert.Equal("4,2K", AgentFormat.Tokens(4_280, Portuguese));
        Assert.Equal("$12.40", AgentFormat.Cost(12.4, English));
        Assert.Equal("$1,234", AgentFormat.Cost(1234, English));
        Assert.Equal("$12K", AgentFormat.Cost(12_345, English));
        Assert.Equal("$0,42", AgentFormat.Cost(0.42, Portuguese));
        Assert.Equal("0:42", AgentFormat.Clock(42));
        Assert.Equal("1:02:05", AgentFormat.Clock(3725));
        Assert.Equal("0:00", AgentFormat.Clock(-5));
        Assert.Equal("21%", AgentFormat.Percent(0.21, English));
        Assert.Equal("100%", AgentFormat.Percent(3, English));
        Assert.Equal("Sep 22, 2026", AgentFormat.Day(U(1_790_035_200), English));
        Assert.Equal("4m 12s", AgentFormat.Duration(252));
        Assert.Equal("1h 5m", AgentFormat.Duration(3900));
        Assert.Equal("2d 3h", AgentFormat.Duration(2 * 86_400 + 3 * 3600 + 59));
        Assert.Equal("$12.50", AgentFormat.MinimumCost(12.5, 0, English));
        Assert.Equal("≥ $12.50", AgentFormat.MinimumCost(12.5, 1, English));
        Assert.Equal("—", AgentFormat.MinimumCost(0, 2, English));
        Assert.Equal("$0.00", AgentFormat.MinimumCost(0, 0, English));
    }

    // MARK: Claude Code's account check

    private static string UsageResult(string used = "62", string resets = "\"2026-10-15T10:39:59.973389+00:00\"") =>
        $$$"""{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":{{{used}}},"resets_at":{{{resets}}}},"seven_day":{"utilization":9,"resets_at":"2026-10-17T22:59:59.973411+00:00"},"seven_day_opus":{"utilization":70,"resets_at":"2026-10-17T22:59:59Z"},"seven_day_sonnet":null}}""";

    [Fact]
    public void ClaudeCodesUsageReplyBecomesAccountWideWindows()
    {
        var now = T("2026-10-15T07:00:00Z");
        var limits = AgentClaudeServer.Limits(UsageResult(), now)!;
        Assert.Equal((AgentLimitSource.Account, now), (limits.Source, limits.ObservedAt));
        Assert.Equal([62.0, 9, 70], limits.Windows.Select(w => w.UsedPercent));
        Assert.Equal(["claude.fh", "claude.sd", "claude.so"], limits.Windows.Select(w => w.Id));
        Assert.Equal("Opus", limits.Windows[^1].Scope);
        Assert.Equal(T("2026-10-15T10:39:59.973389Z"), limits.Windows[0].ResetsAt);
        Assert.Null(limits.Windows[0].EstimatedResetsAt);
        Assert.Null(AgentClaudeServer.Limits("""{"rate_limits_available":false}""", now));
        Assert.Null(AgentClaudeServer.Limits("""{"rate_limits_available":true,"rate_limits":null}""", now));
        foreach (var invalid in new[] { "true", "\"62\"", "null" })
            Assert.Equal([AgentLimitKind.Weekly, AgentLimitKind.Weekly], AgentClaudeServer.Limits(UsageResult(invalid), now)!.Windows.Select(w => w.Kind));
        Assert.Equal(0, AgentClaudeServer.Limits(UsageResult("-1"), now)!.Windows[0].UsedPercent);
        Assert.Equal(100, AgentClaudeServer.Limits(UsageResult("101"), now)!.Windows[0].UsedPercent);
        var idle = AgentClaudeServer.Limits(UsageResult("0", "null"), now)!;
        Assert.True(idle.Windows[0] is { Kind: AgentLimitKind.Session, UsedPercent: 0, ResetsAt: null });
        Assert.Equal([AgentLimitKind.Weekly, AgentLimitKind.Weekly], AgentClaudeServer.Limits(UsageResult("0", "\"2026-10-15T06:00:00Z\""), now)!.Windows.Select(w => w.Kind));
        Assert.Equal(AgentLimitKind.Session, AgentClaudeServer.Current(idle, now.AddHours(4))!.Windows[0].Kind);
        Assert.Equal(AgentLimitKind.Weekly, AgentClaudeServer.Current(idle, now.AddHours(5))!.Windows[0].Kind);
        Assert.Equal([AgentLimitKind.Weekly, AgentLimitKind.Weekly], AgentClaudeServer.Current(limits, T("2026-10-15T11:00:00Z"))!.Windows.Select(w => w.Kind));
        Assert.Null(AgentClaudeServer.Current(limits, T("2026-10-18T00:00:00Z")));
    }

    [Fact]
    public void ClaudeCodeIsAskedOverStdinAndRarelyAfterFailures()
    {
        Assert.Equal(60, AgentClaudeServer.Interval(null, pageVisible: true));
        Assert.Equal(300, AgentClaudeServer.Interval(null, pageVisible: false));
        Assert.Equal(300, AgentClaudeServer.Interval(AgentClaudeServer.Failure.Unreachable, pageVisible: true));
        foreach (var failure in new[] { AgentClaudeServer.Failure.Missing, AgentClaudeServer.Failure.NeedsSignIn, AgentClaudeServer.Failure.Outdated })
            Assert.Equal(1_800, AgentClaudeServer.Interval(failure, pageVisible: true));
        Assert.Equal([300.0, 300, 600, 1_200, 1_800, 1_800], new[] { 0, 1, 2, 3, 4, 9 }.Select(n => AgentClaudeServer.Interval(AgentClaudeServer.Failure.Unreachable, n, true)));
        Assert.Equal("{\"type\":\"control_request\",\"request_id\":\"usage\",\"request\":{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}\n", AgentClaudeServer.UsageRequest);
        Assert.StartsWith("{\"type\":\"control_request\",\"request_id\":\"initialize\"", AgentClaudeServer.InitializeRequest);
        Assert.Null(AgentClaudeServer.Response("""{"type":"assistant"}""", "usage"));
        Assert.Null(AgentClaudeServer.Response("""{"type":"control_response","response":{"subtype":"success","request_id":"initialize"}}""", "usage"));
        var success = AgentClaudeServer.Response("""{"type":"control_response","response":{"subtype":"success","request_id":"usage","response":{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":100,"resets_at":"2099-10-15T10:40:00Z"}}}}}""", "usage")!.Value;
        Assert.Null(success.Failure);
        Assert.True(AgentClaudeServer.Available(success.Result!));
        Assert.Equal(100, AgentClaudeServer.Limits(success.Result!, T("2026-10-15T07:00:00Z"))!.Windows[0].UsedPercent);
        Assert.Equal(AgentClaudeServer.Failure.Outdated, AgentClaudeServer.Response("""{"type":"control_response","response":{"subtype":"error","request_id":"usage","error":"Unsupported control request subtype: get_usage"}}""", "usage")!.Value.Failure);
        Assert.Equal(AgentClaudeServer.Failure.NeedsSignIn, AgentClaudeServer.Response("""{"type":"control_response","response":{"subtype":"error","request_id":"usage","error":"Please login"}}""", "usage")!.Value.Failure);
        Assert.Equal(AgentClaudeServer.Failure.Unreachable, AgentClaudeServer.Response("""{"type":"control_response","response":{"subtype":"error","request_id":"usage","error":"boom"}}""", "usage")!.Value.Failure);
        // A copy saved before the check began answered in place of the account.
        var started = T("2026-10-15T07:00:00Z");
        Assert.Null(AgentClaudeServer.Observed(started, started.AddMinutes(-30), started.AddSeconds(5)));
        Assert.Equal(started.AddSeconds(2), AgentClaudeServer.Observed(started, started.AddSeconds(2), started.AddSeconds(5)));
        Assert.Equal(started.AddSeconds(5), AgentClaudeServer.Observed(started, null, started.AddSeconds(5)));
        var reading = ClaudeAccountReading.Parse("""{"oauthAccount":{"accountUuid":"a","emailAddress":" me@example.com ","organizationUuid":"o","organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_5x"},"cachedUsageUtilization":{"fetchedAtMs":1790000000000}}"""u8);
        Assert.Equal(new ClaudeAccountReading.AccountIdentity("a", "me@example.com", "o"), reading.Identity);
        Assert.Equal(new AgentPlan("Max", 100), reading.Plan);
        Assert.Equal(U(1_790_000_000), reading.UsageFetched);
        Assert.Equal(ClaudeAccountReading.None, ClaudeAccountReading.Parse("not json"u8));
        var environment = AgentClaudeServer.Environment(new Dictionary<string, string> { ["PATH"] = "x", ["ANTHROPIC_API_KEY"] = "k", ["CLAUDECODE"] = "1", ["DISABLE_TELEMETRY"] = "1" },
                                                        """{"env":{"HTTPS_PROXY":"http://proxy","DISABLE_ERROR_REPORTING":true}}""");
        Assert.Equal(new Dictionary<string, string> { ["PATH"] = "x", ["DISABLE_TELEMETRY"] = "1", ["DISABLE_AUTOUPDATER"] = "1", ["HTTPS_PROXY"] = "http://proxy", ["DISABLE_ERROR_REPORTING"] = "true" },
                     environment);
    }

    // MARK: Cursor

    private static string Base64Url(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Jwt(string sub) => Base64Url("""{"alg":"HS256","typ":"JWT"}""") + "." + Base64Url($$"""{"sub":"{{sub}}","exp":1790000000}""") + ".c2lnbmF0dXJl";

    [Fact]
    public void CursorTokensArePlainOrSealedAndNameTheirUser()
    {
        var jwt = Jwt("auth0|user_123");
        Assert.Equal(jwt, CursorToken.Jwt(jwt, null));
        Assert.Equal(jwt, CursorToken.Jwt("\"" + jwt + "\"", null));
        // A sealed token goes through the unseal function: here a fake that reverses the bytes.
        var sealedBytes = Encoding.UTF8.GetBytes(jwt).Reverse().ToArray();
        var stored = CursorToken.SealedPrefix + Convert.ToBase64String(sealedBytes);
        Assert.Equal(jwt, CursorToken.Jwt(stored, bytes => [.. bytes.Reverse()]));
        Assert.Null(CursorToken.Jwt(stored, null));
        Assert.Null(CursorToken.Jwt(stored, _ => null));
        Assert.Null(CursorToken.Jwt(stored, _ => throw new InvalidOperationException()));
        Assert.Null(CursorToken.Jwt(CursorToken.SealedPrefix + "%%%", _ => []));
        Assert.Null(CursorToken.Jwt("not a token", null));
        Assert.Equal("user_123", CursorToken.UserId(jwt));
        Assert.Equal("user_9", CursorToken.UserId(Jwt("user_9")));
        Assert.Null(CursorToken.UserId("a.b.c"));
        Assert.Equal("WorkosCursorSessionToken=user_123%3A%3A" + jwt, CursorToken.Cookie(jwt));
    }

    [Fact]
    public void CursorUsageSummariesBecomeAnIncludedUsageWindowOrSayTheyChanged()
    {
        var pro = CursorUsageSummary.Parse("""{"billingCycleStart":"2026-11-01T00:00:00.000Z","billingCycleEnd":"2026-12-01T00:00:00.000Z","membershipType":"pro","limitType":"user","individualUsage":{"plan":{"enabled":true,"used":1250,"limit":2000,"remaining":750,"totalPercentUsed":62.5},"onDemand":{"enabled":false}},"teamUsage":{}}""");
        Assert.True(pro.Recognized);
        Assert.Equal("pro", pro.Plan);
        Assert.Equal(T("2026-12-01T00:00:00Z"), pro.Window!.ResetsAt);
        Assert.Equal((CursorUsageSummary.WindowId, 62.5, (int?)30 * 1440), (pro.Window.Id, pro.Window.UsedPercent, pro.Window.Minutes));
        var limits = pro.Limits(T("2026-11-22T10:00:00Z"))!;
        Assert.Equal((AgentProvider.Cursor, AgentLimitSource.Account), (limits.Provider, limits.Source));
        // An Enterprise seat reports under the team, with epoch milliseconds and no percent.
        var enterprise = CursorUsageSummary.Parse("""{"billingCycleStart":1793491200000,"billingCycleEnd":1796083200000,"membership":"enterprise","teamUsage":{"plan":{"used":"40","limit":"500"}}}""");
        Assert.True(enterprise.Recognized);
        Assert.Equal("enterprise", enterprise.Plan);
        Assert.Equal(8, enterprise.Window!.UsedPercent, 9);
        Assert.Equal(AgentTime.FromUnixMilliseconds(1_796_083_200_000), enterprise.Window.ResetsAt);
        var fallback = CursorUsageSummary.Parse("""{"membershipType":"pro_plus","individualUsage":{"plan":{"used":30,"limit":120}}}""");
        Assert.Equal(25, fallback.Window!.UsedPercent, 9);
        Assert.Null(fallback.Window.ResetsAt);
        Assert.Equal(40, CursorUsageSummary.Parse("""{"individualUsage":{"plan":{"remaining":60,"limit":100}}}""").Window!.UsedPercent, 9);
        Assert.Equal(100, CursorUsageSummary.Parse("""{"individualUsage":{"plan":{"percentUsed":130}}}""").Window!.UsedPercent);
        foreach (var malformed in new[] { null, "", "not json", "[]", "{}", """{"individualUsage":{"plan":{"used":5,"limit":0}}}""",
                                          """{"individualUsage":"oops"}""", """{"membershipType":"free"}""" })
        {
            Assert.Same(CursorUsageSummary.Unrecognized, CursorUsageSummary.Parse(malformed));
        }
        Assert.Null(CursorUsageSummary.Unrecognized.Limits(T("2026-11-22T10:00:00Z")));
    }
}
