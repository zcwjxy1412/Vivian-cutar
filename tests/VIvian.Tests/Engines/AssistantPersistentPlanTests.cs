using System.Text.Json.Nodes;
using System.Net;
using VIvian.App;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Assistant;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class AssistantPersistentPlanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("furina-plans-").FullName;
    private static readonly Guid A = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid B = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid PlanId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static PreparedExecutionPlan Plan() => new(PlanId, DateTimeOffset.UtcNow.AddMinutes(5),
        [new(0, GameId.ZenlessZoneZero, A, "ZZZ fixture", "routine", "rev-a", 1, "saved_account_settings", "resource-a"),
         new(1, GameId.StarRail, B, "HSR fixture", "routine", "rev-b", 2, "saved_account_settings", "resource-b")]);
    private static string Request => $$"""{"items":[{"game":"zzz","account_id":"{{A:D}}"},{"game":"starrail","account_id":"{{B:D}}"}]}""";
    private static AssistantTools Tools(FakeService service) => new(() => [], _ => Task.FromResult<IReadOnlyList<RunRecord>>([]), executionPlans: service)
        { ScheduleProposalsEnabled = true };

    [Fact]
    public async Task PrepareUsesHostSnapshotsInRequestedOrderWithoutSubmitting()
    {
        var service = new FakeService(Plan());
        var result = await Tools(service).InvokeAsync("propose_run", Request, default);
        Assert.NotNull(result.ExecutionPlan);
        Assert.Equal(service.Plan.PlanId, result.ExecutionPlan.PlanId);
        Assert.Equal(service.Plan.Items, result.ExecutionPlan.Items);
        Assert.Equal(new[] { GameId.ZenlessZoneZero, GameId.StarRail }, service.Request!.Select(i => i.Game));
        Assert.Equal(0, service.Submissions);
        Assert.Contains(PlanId.ToString("D"), result.Content);
        Assert.Null(result.RunPlan);
    }

    [Theory]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":null}")]
    [InlineData("{\"items\":[{\"game\":null,\"account_id\":\"10000000-0000-4000-8000-000000000001\"}]}")]
    [InlineData("{\"items\":[{\"game\":5,\"account_id\":\"10000000-0000-4000-8000-000000000001\"}]}")]
    [InlineData("{\"items\":[{\"game\":\"zzz\",\"account_id\":null}]}")]
    [InlineData("{\"items\":[{\"game\":\"zzz\",\"account_id\":\"bad\"}]}")]
    [InlineData("{\"items\":[{\"game\":\"zzz\",\"account_id\":\"10000000-0000-4000-8000-000000000001\",\"task_id\":\"shell\"}]}")]
    public async Task InvalidItemsNeverReachPreparation(string json)
    {
        var service = new FakeService(Plan());
        var result = await Tools(service).InvokeAsync("propose_run", json, default);
        Assert.Null(service.Request);
        Assert.Null(result.ExecutionPlan);
        Assert.NotNull(JsonNode.Parse(result.Content)!["error"]);
    }

    [Fact]
    public async Task DuplicateMixedAndMismatchedTargetsAreRejected()
    {
        var service = new FakeService(Plan());
        var args = JsonNode.Parse(Request)!.AsObject();
        args["game"] = "starrail";
        Assert.Null((await Tools(service).InvokeAsync("propose_run", args.ToJsonString(), default)).ExecutionPlan);
        args.Remove("game");
        args["items"]!.AsArray()[1] = args["items"]![0]!.DeepClone();
        Assert.Null((await Tools(service).InvokeAsync("propose_run", args.ToJsonString(), default)).ExecutionPlan);
        Assert.Null(service.Request);
        service.Plan = service.Plan with { Items = service.Plan.Items.Reverse().ToArray() };
        Assert.Null((await Tools(service).InvokeAsync("propose_run", Request, default)).ExecutionPlan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyHostLabelsCannotBecomeConfirmationCards(bool name)
    {
        var plan = Plan();
        var bad = name ? plan.Items[0] with { AccountName = "" } : plan.Items[0] with { ResourceSummary = " " };
        var service = new FakeService(plan with { Items = [bad, plan.Items[1]] });
        var result = await Tools(service).InvokeAsync("propose_run", Request, default);
        Assert.Null(result.ExecutionPlan);
        Assert.NotNull(JsonNode.Parse(result.Content)!["error"]);
    }

    [Fact]
    public async Task CapabilityListingRecognizesHostBackedPlansWithoutBypassingRuntimeAvailability()
    {
        var service = new FakeService(Plan());
        var tools = new AssistantTools(() => [new(GameId.Genshin, "fixture", "fixture", "1", "fixture", true),
            new(GameId.ZenlessZoneZero, "fixture", "fixture", "1", "fixture", false)],
            _ => Task.FromResult<IReadOnlyList<RunRecord>>([]), executionPlans: service) { ScheduleProposalsEnabled = true };
        var games = JsonNode.Parse((await tools.InvokeAsync("list_games", "{}", default)).Content)!.AsArray();
        Assert.True(games[0]!["ai_run_proposals"]!.GetValue<bool>());
        Assert.False(games[1]!["ai_run_proposals"]!.GetValue<bool>());
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task ExactPlanQueryIsTypedAndUnknownNeverFallsBackOrSubmits()
    {
        var service = new FakeService(Plan()) { State = ExecutionPlanState.Interrupted };
        var tools = Tools(service);
        var result = await tools.InvokeAsync("get_batch_progress", $$"""{"plan_id":"{{PlanId:D}}"}""", default);
        var receipt = JsonNode.Parse(result.Content)!;
        Assert.Equal("Interrupted", receipt["state"]!.GetValue<string>());
        Assert.Equal(PlanId.ToString("D"), receipt["plan_id"]!.GetValue<string>());
        Assert.Equal(0, service.Submissions);
        foreach (var json in new[] { "{\"plan_id\":null}", "{\"plan_id\":\"bad\"}",
            $$"""{"plan_id":"{{Guid.NewGuid():D}}"}""", $$"""{"plan_id":"{{PlanId:D}}","batch_id":"{{A:D}}"}""" })
            Assert.NotNull(JsonNode.Parse((await tools.InvokeAsync("get_batch_progress", json, default)).Content)!["error"]);
    }

    [Fact]
    public async Task LimitsOptInAndMissingServiceNeverPrepareOrFallBack()
    {
        var service = new FakeService(Plan());
        var tools = Tools(service);
        var items = new JsonArray(Enumerable.Range(0, 33).Select(_ => JsonNode.Parse(Request)!["items"]![0]!.DeepClone()).ToArray());
        Assert.Null((await tools.InvokeAsync("propose_run", new JsonObject { ["items"] = items }.ToJsonString(), default)).ExecutionPlan);
        tools.ScheduleProposalsEnabled = false;
        Assert.Null((await tools.InvokeAsync("propose_run", Request, default)).ExecutionPlan);
        Assert.Null(service.Request);
        var absent = new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<RunRecord>>([])) { ScheduleProposalsEnabled = true };
        Assert.NotNull(JsonNode.Parse((await absent.InvokeAsync("get_batch_progress", $$"""{"plan_id":"{{PlanId:D}}"}""", default)).Content)!["error"]);
        Assert.Null((await absent.InvokeAsync("propose_run", Request, default)).ExecutionPlan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionPublishesPreparedPlansOnlyAfterACompleteTurn(bool truncated)
    {
        var service = new FakeService(Plan());
        using var http = new HttpClient(new PlanModel(truncated));
        var session = new AssistantSession(new ChatCompletionsClient(http), Tools(service), "fixture");
        var endpoint = new AssistantEndpoint("https://example.invalid/v1", "fixture", "fixture-key");
        if (truncated)
        {
            await Assert.ThrowsAsync<AssistantException>(() => session.AskAsync(endpoint, "prepare", default));
            Assert.Empty(session.LastExecutionPlans);
        }
        else
        {
            await session.AskAsync(endpoint, "prepare", default);
            Assert.Equal(PlanId, Assert.Single(session.LastExecutionPlans).PlanId);
        }
        Assert.Empty(session.LastRunPlans);
        Assert.Empty(session.LastProposals);
        Assert.Equal(0, service.Submissions);
    }

    [Fact]
    public async Task OrderedCardSubmitsOnceWithoutLegacySingleStarRailGuard()
    {
        var service = new FakeService(Plan());
        var vm = Vm(service);
        var card = new ProposalCard(service.Plan, c => vm.ConfirmExecutionPlanAsync(c, service.Plan));
        Assert.Null(card.Target);
        Assert.True(card.Lines.IndexOf("ZZZ fixture", StringComparison.Ordinal) < card.Lines.IndexOf("HSR fixture", StringComparison.Ordinal));
        var released = false;
        vm.ExclusiveActionStarting += () => released = true;
        service.BeforeSubmit = ct => { Assert.True(released); vm.CancelRequest(); Assert.False(ct.CanBeCanceled); };
        await vm.ConfirmExecutionPlanAsync(card, service.Plan);
        await vm.ConfirmExecutionPlanAsync(card, service.Plan);
        Assert.Equal(1, service.Submissions);
        Assert.Contains(PlanId.ToString("D"), card.State);
        Assert.Contains("已接受", card.State);
        Assert.DoesNotContain("全部完成", card.State);
    }

    [Theory]
    [InlineData(ExecutionPlanDisposition.Existing, "已有")]
    [InlineData(ExecutionPlanDisposition.Conflict, "冲突")]
    [InlineData(ExecutionPlanDisposition.Rejected, "拒绝")]
    public async Task SubmissionDispositionIsNotInferredFromText(ExecutionPlanDisposition disposition, string expected)
    {
        var service = new FakeService(Plan()) { Disposition = disposition };
        var vm = Vm(service);
        var card = new ProposalCard(service.Plan, _ => Task.CompletedTask);
        await vm.ConfirmExecutionPlanAsync(card, service.Plan);
        Assert.Contains(expected, card.State);
        Assert.False(card.CanConfirm);
    }

    [Fact]
    public async Task SubmissionFailureRetainsPlanIdForReadbackWithoutRetry()
    {
        var service = new FakeService(Plan()) { BeforeSubmit = _ => throw new IOException("private-detail") };
        var vm = Vm(service);
        var card = new ProposalCard(service.Plan, _ => Task.CompletedTask);
        await vm.ConfirmExecutionPlanAsync(card, service.Plan);
        Assert.Contains(PlanId.ToString("D"), card.State);
        Assert.Contains("查回", card.State);
        Assert.DoesNotContain("private-detail", card.State);
        Assert.False(card.CanConfirm);
        Assert.Equal(1, service.Submissions);
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("expire")]
    [InlineData("invalidate")]
    public async Task InvalidOrDeclinedPlanDoesNotReleaseOrSubmit(string mode)
    {
        var service = new FakeService(Plan());
        if (mode == "expire") service.Plan = service.Plan with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        AssistantViewModel? vm = null;
        vm = Vm(service, () => { if (mode == "invalidate") vm!.InvalidateProposals(); return mode != "decline"; });
        var events = 0;
        vm.ExclusiveActionStarting += () => events++;
        await vm.ConfirmExecutionPlanAsync(new ProposalCard(service.Plan, _ => Task.CompletedTask), service.Plan);
        Assert.Equal(0, events);
        Assert.Equal(0, service.Submissions);
    }

    [Fact]
    public async Task InvalidationDuringReleaseNeverSubmits()
    {
        var service = new FakeService(Plan());
        var vm = Vm(service);
        vm.ExclusiveActionStarting += vm.InvalidateProposals;
        await vm.ConfirmExecutionPlanAsync(new ProposalCard(service.Plan, _ => Task.CompletedTask), service.Plan);
        Assert.Equal(0, service.Submissions);
    }

    private AssistantViewModel Vm(FakeService service, Func<bool>? confirm = null) => new(new ThemeAssets(_root, _root), new AssistantSettingsStore(_root), Tools(service),
        _ => false, _root, () => Task.CompletedTask, _ => throw new InvalidOperationException("legacy path must not run"),
        (_, _, _) => confirm?.Invoke() ?? true, service);

    private sealed class PlanModel(bool truncated) : HttpMessageHandler
    {
        private int _calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var first = ++_calls == 1;
            if (!first)
            {
                var result = body["messages"]!.AsArray().Last()!;
                Assert.Equal("ordered-plan", result["tool_call_id"]!.GetValue<string>());
                Assert.Contains(PlanId.ToString("D"), result["content"]!.GetValue<string>());
            }
            var message = first ? new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
            { ["id"] = "ordered-plan", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "propose_run", ["arguments"] = Request } }) }
                : new JsonObject { ["role"] = "assistant", ["content"] = "请核对计划。" };
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject
            { ["message"] = message, ["finish_reason"] = first ? "tool_calls" : truncated ? "length" : "stop" }) }.ToJsonString()) };
        }
    }

    private sealed class FakeService(PreparedExecutionPlan plan) : IExecutionPlanService
    {
        public PreparedExecutionPlan Plan { get; set; } = plan;
        public ExecutionPlanState State { get; init; } = ExecutionPlanState.Accepted;
        public ExecutionPlanDisposition Disposition { get; init; } = ExecutionPlanDisposition.Accepted;
        public IReadOnlyList<ExecutionPlanRequestItem>? Request { get; private set; }
        public int Submissions { get; private set; }
        public Action<CancellationToken>? BeforeSubmit { get; set; }
        public Task<ExecutionPlanPreparation> PrepareAsync(IReadOnlyList<ExecutionPlanRequestItem> items, CancellationToken ct)
        { Request = items; return Task.FromResult(new ExecutionPlanPreparation(Plan, null)); }
        public Task<ExecutionPlanSubmission> SubmitConfirmedAsync(PreparedExecutionPlan plan, CancellationToken ct)
        { Submissions++; BeforeSubmit?.Invoke(ct); return Task.FromResult(new ExecutionPlanSubmission(Disposition, Receipt(), "fixture")); }
        public Task<ExecutionPlanReceipt?> ReadAsync(Guid planId, CancellationToken ct) => Task.FromResult(planId == Plan.PlanId ? Receipt() : null);
        private ExecutionPlanReceipt Receipt() => new(Plan, "fixture-hash", State, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Plan.Items.Select(i => new ExecutionPlanItemReceipt(i, Guid.NewGuid(), ExecutionPlanItemState.Pending, [], null)).ToArray());
    }
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
