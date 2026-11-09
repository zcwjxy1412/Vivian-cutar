using System.IO.Pipes;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiLaunchGuardTests
{
    [Fact]
    public async Task ANameThatDoesNotExistNeverReachesTheSupervisor()
    {
        await using var fixture = new BetterGiFixture();
        var adapter = fixture.Adapter(BetterGiArguments.OneDragon("不存在").Request,
            inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation));

        var preflight = await adapter.PreflightAsync(fixture.Context, default);
        Assert.Equal(ReasonCodes.PreflightError, preflight.ReasonCode);
        var error = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));
        Assert.True(error.NothingStarted);
        Assert.Equal(ReasonCodes.PreflightError, error.ReasonCode);
        Assert.Equal(0, fixture.Supervisor.Calls);
        Assert.Equal(0, fixture.Logs.Opens);
        Assert.False(fixture.Registry.IsBusy);
    }

    [Fact]
    public async Task AFileNameMatchIsInsufficientAndDuplicateLogicalNamesAreAmbiguous()
    {
        await using var fixture = new BetterGiFixture();
        var adapter = fixture.Adapter(BetterGiArguments.OneDragon("目标 配置").Request);
        var file = Path.Combine(fixture.Root, "User", "OneDragon", "目标 配置.json");
        File.WriteAllText(file, """{ "Name": "实际名称" }""");
        var missing = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));
        Assert.True(missing.NothingStarted);

        File.WriteAllText(file, """{ "Name": "目标 配置" }""");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "旧备份.json"), """{ "name": "目标 配置" }""");
        var duplicate = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));
        Assert.True(duplicate.NothingStarted);
        Assert.Equal(0, fixture.Supervisor.Calls);
    }

    [Fact]
    public async Task CurrentTaskOrderAndResumeMarkerMatchTheUpstreamPlan()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "User", "OneDragon", "默认配置.json");
        File.WriteAllText(path, """
            {
              "Name": "默认配置",
              "TaskEnabledList": { "craft": true, "daily": true, "domain": false },
              "TaskDefinitions": { "craft": "合成树脂", "daily": "领取每日奖励", "domain": "自动秘境" },
              "TaskOrder": [ "craft", "daily", "domain" ],
              "NextTaskId": "daily",
              "CompletionAction": "无"
            }
            """);

        var plan = await BetterGiConfiguration.InspectOneDragonAsync(fixture.Root);

        Assert.Equal(["领取每日奖励"], plan.EnabledTasks);
        Assert.Equal("无", plan.CompletionAction);
    }

    [Fact]
    public async Task EnabledGroupMissingFromTheInstallationBlocksThePlan()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "User", "OneDragon", "日常.json");
        File.WriteAllText(path, """{ "Name": "日常", "TaskEnabledList": { "缺失组": true } }""");
        var adapter = fixture.Adapter(BetterGiArguments.OneDragon("日常").Request);

        var error = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));

        Assert.True(error.NothingStarted);
        Assert.Equal(0, fixture.Supervisor.Calls);
    }

    [Fact]
    public async Task DomainResinCapsRemainSeparatedFromResinPriorityAndSalvage()
    {
        await using var fixture = new BetterGiFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "User", "config.json"), """
            {
              "autoDomainConfig": {
                "specifyResinUse": true,
                "resinPriorityList": [ "原粹树脂", "浓缩树脂" ],
                "originalResinUseCount": 6, "originalResin20UseCount": 1, "originalResin40UseCount": 2,
                "condensedResinUseCount": 3, "transientResinUseCount": 4,
                "fragileResinUseCount": 5, "autoArtifactSalvage": true
              }
            }
            """);

        var plan = await BetterGiConfiguration.InspectDomainResinAsync(fixture.Root);

        Assert.True(plan.SpecifyResinUse);
        Assert.Equal(["原粹树脂", "浓缩树脂"], plan.Priority);
        Assert.Equal((6, 1, 2, 3, 4, 5), (plan.Original, plan.Original20, plan.Original40, plan.Condensed, plan.Transient, plan.Fragile));
        Assert.True(plan.HasExplicitBudget);
        Assert.True(plan.AutoArtifactSalvage);
    }

    [Fact]
    public async Task MisCasedGlobalResinFieldsDoNotEnableExplicitLimits()
    {
        await using var fixture = new BetterGiFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "User", "config.json"),
            """{ "autoDomainConfig": { "SpecifyResinUse": true, "OriginalResin20UseCount": 3 } }""");

        var plan = await BetterGiConfiguration.InspectDomainResinAsync(fixture.Root);

        Assert.False(plan.SpecifyResinUse);
        Assert.Equal(0, plan.Original20);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AProfileWithSalvageOrUnboundedResinDoesNotReachLaunch(bool salvage, bool specifyResin)
    {
        await using var fixture = new BetterGiFixture();
        var user = Path.Combine(fixture.Root, "User");
        File.WriteAllText(Path.Combine(user, "OneDragon", "默认配置.json"),
            """{ "Name": "默认配置", "TaskEnabledList": { "自动秘境": true } }""");
        File.WriteAllText(Path.Combine(user, "config.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            autoDomainConfig = new { autoArtifactSalvage = salvage, specifyResinUse = specifyResin },
        }));
        var adapter = fixture.Adapter(BetterGiArguments.OneDragon("默认配置").Request);

        var error = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));

        Assert.True(error.NothingStarted);
        Assert.Equal(0, fixture.Supervisor.Calls);
    }

    [Fact]
    public async Task ExplicitResinCountsDoNotOpenTheUnverifiedDomainBudgetPath()
    {
        await using var fixture = new BetterGiFixture();
        var user = Path.Combine(fixture.Root, "User");
        File.WriteAllText(Path.Combine(user, "OneDragon", "默认配置.json"),
            """{ "Name": "默认配置", "TaskEnabledList": { "自动秘境": true }, "DomainName": "虹灵的净土" }""");
        File.WriteAllText(Path.Combine(user, "config.json"),
            """{ "autoDomainConfig": { "specifyResinUse": true, "originalResin20UseCount": 1, "autoArtifactSalvage": false } }""");
        var adapter = fixture.Adapter(BetterGiArguments.OneDragon("默认配置").Request);
        var error = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));

        Assert.True(error.NothingStarted);
        Assert.Contains("秘境仅可保存和预览", error.Message);
        Assert.Equal(0, fixture.Supervisor.Calls);
    }

    [Fact]
    public async Task AProfileRemovedAfterPreflightIsRecheckedAtLaunch()
    {
        await using var fixture = new BetterGiFixture();
        var adapter = fixture.Adapter(BetterGiArguments.OneDragon("默认配置").Request,
            inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation));
        Assert.Equal(ReasonCodes.RealAdapterUnverified, (await adapter.PreflightAsync(fixture.Context, default)).ReasonCode);
        File.Delete(Path.Combine(fixture.Root, "User", "OneDragon", "默认配置.json"));

        var error = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));
        Assert.True(error.NothingStarted);
        Assert.Equal(0, fixture.Supervisor.Calls);
    }

    [Fact]
    public async Task AnInstanceAppearingBetweenPreflightAndLaunchBlocksWithoutOpeningLogs()
    {
        await using var fixture = new BetterGiFixture();
        var adapter = fixture.Adapter(inspect: BetterGiFixture.Report(CompatibilityClass.PendingValidation));
        Assert.Equal(ReasonCodes.RealAdapterUnverified, (await adapter.PreflightAsync(fixture.Context, default)).ReasonCode);
        fixture.Probe.Running = 1;

        var error = await Assert.ThrowsAsync<EngineStartException>(
            () => adapter.LaunchAsync(fixture.Context, fixture.Supervisor, default));
        Assert.Equal(ReasonCodes.ForeignProcess, error.ReasonCode);
        Assert.True(error.NothingStarted);
        Assert.Equal(0, fixture.Supervisor.Calls);
        Assert.Equal(0, fixture.Logs.Opens);
        Assert.False(fixture.Registry.IsBusy);
    }

    [Fact]
    public void AnotherInstallationCannotReserveTheSameUserSlot()
    {
        var registry = new BetterGiRunRegistry();
        var first = Guid.NewGuid();
        Assert.True(registry.TryAcquire(first));
        Assert.False(registry.TryAcquire(Guid.NewGuid()));
        registry.Release(first, CleanupState.Unconfirmed);
        Assert.True(registry.IsBusy);
    }

    [Fact]
    public async Task ExitZeroWithNoAttributedStartupCouldHaveForwardedTheTask()
    {
        await using var fixture = new BetterGiFixture();
        var (run, group) = await fixture.LaunchAsync(fixture.Adapter(contractVerified: true));
        group.Exit(0);
        var verdict = await run.WaitForVerdictAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.CleanupUnconfirmed, verdict.ReasonCode);
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.True(fixture.Registry.IsBusy);
    }

    [Fact]
    public async Task EmergencyStopWithoutPrimaryEvidenceKeepsTheSlotBlocked()
    {
        await using var fixture = new BetterGiFixture();
        var (run, group) = await fixture.LaunchAsync(fixture.Adapter());
        var stop = await run.RequestStopAsync(StopMode.Emergency, default);

        Assert.Equal(0, group.ActiveProcessCount);
        Assert.Equal(CleanupState.Unconfirmed, stop.Cleanup);
        Assert.Equal(CleanupState.Unconfirmed, await run.ReconcileAsync(default));
        await run.DisposeAsync();
        Assert.True(fixture.Registry.IsBusy);
    }

    [Fact]
    public void AnIpcRootAtAnotherPathBlocksWithoutConnecting()
    {
        var pipe = "VIvian-test-bgi-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipe);

        Assert.True(BetterGiProcessProbe.RootPipeMayExist(pipe));
        Assert.False(server.IsConnected);
        Assert.False(BetterGiProcessProbe.RootPipeMayExist(pipe + "-absent"));
    }
}
