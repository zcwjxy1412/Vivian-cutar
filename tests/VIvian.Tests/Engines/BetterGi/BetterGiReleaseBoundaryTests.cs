using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class BetterGiReleaseBoundaryTests
{
    [Fact]
    public async Task FailedRunRegistrationLeavesConfigurationAndRecoveryUntouched()
    {
        await using var fixture = new BetterGiFixture();
        var account = Guid.NewGuid();
        var name = BetterGiAccountPlan.SourceProfileName(account);
        var profilePath = Path.Combine(fixture.Root, "User", "OneDragon", name + ".json");
        File.WriteAllText(profilePath, System.Text.Json.JsonSerializer.Serialize(new { Name = name,
            CompletionAction = "无", TaskEnabledList = new Dictionary<string, bool> { ["领取每日奖励"] = true } }));
        var original = File.ReadAllText(profilePath);
        var owner = Directory.CreateDirectory(Path.Combine(fixture.Root, "owner")).FullName;
        var lockPath = Path.Combine(fixture.Root, "desktop.lock");
        using (var lease = DesktopOperationLease.AcquireAt(lockPath, owner))
            await Assert.ThrowsAsync<IOException>(() => BetterGiNativeDaily.RunCoreAsync(fixture.Root, account,
                BetterGiNativeDaily.Profile(fixture.Root, account).Read().Revision, Path.Combine(owner, "reports"), lease,
                (_, _) => throw new Exception("must not launch"), default, childBatchId: Guid.NewGuid(),
                beforeRun: (_, _, _) => throw new IOException("registration failed")));
        Assert.Equal(original, File.ReadAllText(profilePath));
        Assert.False(File.Exists(Path.Combine(owner, "native-recovery.json")));
        Assert.False(BetterGiNativeDaily.Profile(fixture.Root, account).HasPending);
        using (DesktopOperationLease.AcquireAt(lockPath, owner)) { }
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task NativeReleaseFailureRetainsRecoveryAndNeverStartsTheNextAccount(bool failDispose, bool cancel)
    {
        await using var fixture = new BetterGiFixture();
        var db = new LocalDatabase(Path.Combine(fixture.Root, "test.db"));
        await db.InitializeAsync();
        var first = await db.AddAccountAsync(GameId.Genshin, "A", "", null);
        var second = await db.AddAccountAsync(GameId.Genshin, "B", "", null);
        foreach (var account in new[] { first, second })
        {
            var identity = account.Id == first.Id ? "130****01" : "130****02";
            await db.SaveLoginProfileAsync(account.Id, identity, identity, 0, .95, null, "synthetic");
            var name = BetterGiAccountPlan.SourceProfileName(account.Id);
            File.WriteAllText(Path.Combine(fixture.Root, "User", "OneDragon", name + ".json"),
                System.Text.Json.JsonSerializer.Serialize(new { Name = name, CompletionAction = "无",
                    TaskEnabledList = new Dictionary<string, bool> { ["领取每日奖励"] = true } }));
        }
        var owner = Directory.CreateDirectory(Path.Combine(fixture.Root, "owner")).FullName;
        var lockPath = Path.Combine(fixture.Root, "desktop.lock");
        var executed = new List<Guid>();
        var results = new List<BetterGiDailyAcceptanceResult>();
        using (var lease = DesktopOperationLease.AcquireAt(lockPath, owner))
        {
            var outcome = await new MultiAccountOrchestrator(db, new LoginDriver()).ExecuteAsync(
                GameId.Genshin, [first, second], AccountConfigurationMode.Independent,
                (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
                async (account, _, ct) =>
                {
                    executed.Add(account.Id);
                    var name = BetterGiAccountPlan.SourceProfileName(account.Id);
                    var revision = BetterGiNativeDaily.Profile(fixture.Root, account.Id).Read().Revision;
                    using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var childBatchId = Guid.NewGuid();
                    Guid? registeredRun = null;
                    var result = await BetterGiNativeDaily.RunCoreAsync(fixture.Root, account.Id, revision,
                        Path.Combine(owner, "reports"), lease, (context, _) =>
                        {
                            Assert.Equal(childBatchId, context.QueueId);
                            Assert.Equal(registeredRun, context.RunId);
                            Assert.True(fixture.Registry.TryAcquire(context.RunId));
                            var group = new FakeGroup(context.RunId) { DisposeThrows = failDispose };
                            var logs = new ScriptedLogSource();
                            var run = new BetterGiRun(context.RunId, group, logs.Open(fixture.Root),
                                BetterGiArguments.OneDragon(name).Request!, new BetterGiRunOptions
                                {
                                    LogQuietPeriod = TimeSpan.FromMilliseconds(10),
                                    LogDrainLimit = TimeSpan.FromSeconds(1),
                                    CompletionContractVerifiedOverride = true,
                                }, TimeProvider.System, cleanup => fixture.Registry.Release(context.RunId, cleanup));
                            logs.Write(BgiLog.SuccessfulRun(group.RootProcessId, group.RootStartTime.AddMilliseconds(300), name));
                            logs.Channel.Writer.TryComplete();
                            group.Exit(0);
                            if (cancel) runCancellation.Cancel();
                            return Task.FromResult(run);
                        }, runCancellation.Token, childBatchId: childBatchId, beforeRun: (accountId, runId, _) =>
                        {
                            Assert.Equal(account.Id, accountId);
                            Assert.False(File.Exists(Path.Combine(owner, "native-recovery.json")));
                            registeredRun = runId;
                            return Task.CompletedTask;
                        });
                    results.Add(result);
                    return await BetterGiAccountBatch.RecordDailyAsync(db, result, false, childBatchId);
                }, null, default);
            Assert.Equal(!failDispose && !cancel, outcome.Completed);
            Assert.Equal(failDispose || cancel ? new[] { first.Id } : new[] { first.Id, second.Id }, executed);
            Assert.All(results, result =>
            {
                // Cancellation stops the batch; confirmed process cleanup can still restore configuration.
                Assert.Equal(cancel, result.Cancelled);
                Assert.Equal(failDispose ? CleanupState.Unconfirmed : CleanupState.Confirmed, result.Cleanup);
                Assert.Equal(!failDispose, result.Restored);
            });
            Assert.Equal(failDispose, fixture.Registry.IsBusy);
            Assert.Equal(failDispose, File.Exists(Path.Combine(owner, "native-recovery.json")));
            Assert.Equal(failDispose, BetterGiNativeDaily.Profile(fixture.Root, first.Id).HasPending);
            Assert.Equal(failDispose, File.Exists(Path.Combine(fixture.Root, "User", "config.json.vivian-domain-recovery")));
        }
        if (failDispose)
            Assert.Throws<DesktopRecoveryRequiredException>(() => DesktopOperationLease.AcquireAt(lockPath, owner));
        else
            using (DesktopOperationLease.AcquireAt(lockPath, owner)) { }
    }

    private sealed class LoginDriver : ILoginSwitchDriver
    {
        public Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken ct) => Done();
        public Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken ct) => Done();
        public Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken ct) => Done();
        public Task<LoginSwitchOutcome> StopGameAsync(CancellationToken ct) => Done();
        private static Task<LoginSwitchOutcome> Done() => Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "synthetic"));
    }
}
