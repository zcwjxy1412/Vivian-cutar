using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using Xunit;

namespace VIvian.Tests.Accounts;

public sealed class MultiAccountOrchestratorTests
{
    private static readonly AccountConfigurationMode Unified = AccountConfigurationMode.Unified;

    [Fact]
    public async Task CancellationWhileReadingSettingsDoesNotTouchTheDesktop()
    {
        await using var fixture = await Fixture.CreateAsync("A");
        var driver = new FakeLoginDriver();
        var touches = 0;
        var result = await new MultiAccountOrchestrator(fixture.Database, driver).ExecuteAsync(GameId.StarRail,
            fixture.Accounts, Unified, (_, _) => throw new OperationCanceledException(),
            (_, _, _) => throw new InvalidOperationException("must not run"), null, CancellationToken.None,
            beforeDesktopMutation: () => touches++);
        Assert.False(result.Completed);
        Assert.Equal(0, touches);
        Assert.Equal(0, driver.Stops);
        Assert.Equal("stopped", Assert.Single(await fixture.Database.LoadRecentBatchesAsync()).State);
    }

    [Fact]
    public async Task UnexpectedDriverFailureStillFinalizesBatch()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver { ThrowOnSwitch = new IOException("capture disconnected") };
        var outcome = await new MultiAccountOrchestrator(fixture.Database, driver).ExecuteAsync(GameId.StarRail,
            fixture.Accounts, Unified, fixture.LoadSettingsAsync,
            (_, _, _) => throw new InvalidOperationException("must not run"), null, CancellationToken.None);
        Assert.False(outcome.Completed);
        Assert.Equal("stopped", Assert.Single(await fixture.Database.LoadRecentBatchesAsync()).State);
        Assert.Contains("capture disconnected", outcome.Detail);
    }

    [Fact]
    public async Task RunsTwoAccountsAndUsesFirstSnapshotInUnifiedMode()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        var calls = new List<(string Name, string Value)>();

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, [fixture.Accounts[0], fixture.Accounts[1]],
            Unified, fixture.LoadSettingsAsync, (account, settings, _) =>
            {
                Assert.Equal(calls.Count, driver.Restarts);
                Assert.Equal(calls.Count + 1, driver.Switches.Count);
                calls.Add((account.Name, settings["borrow_enable"]));
                return Task.FromResult(new BatchTaskResult(Guid.NewGuid(), true, true, true, "日常完成"));
            }, null, CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(new[] { ("A", "true"), ("B", "true") }, calls);
        Assert.Equal(new[] { "153****57", "139****11" }, driver.Switches.Select(s => s.NormalizedPhone));
        // Reuse the login screen before A, then restart only after A's task and cleanup finish.
        Assert.Equal(1, driver.Restarts);
        Assert.Equal(1, driver.Stops);

        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("finished", batch.State);
        Assert.Equal("unified", batch.Mode);
        var results = await fixture.Database.LoadBatchResultsAsync(batch.Id);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("success", result.Result));
    }

    [Fact]
    public async Task KeepsGoingWhenOneAccountTaskFailsButCleanupIsConfirmed()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, fixture.Accounts, Unified,
            fixture.LoadSettingsAsync, (account, _, _) => Task.FromResult(account.Name == "A"
                ? new BatchTaskResult(null, true, true, true, "完成")
                : new BatchTaskResult(null, false, true, true, "模拟任务失败")), null, CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.Succeeded);
        Assert.Equal(1, outcome.Failed);
        Assert.Equal(2, driver.Switches.Count);
    }

    [Fact]
    public async Task StopsBatchWhenCleanupOrConfigurationRestoreIsNotConfirmed()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B", "C");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        var ran = new List<string>();

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, fixture.Accounts, Unified,
            fixture.LoadSettingsAsync, (account, _, _) =>
            {
                ran.Add(account.Name);
                return Task.FromResult(account.Name == "B"
                    ? new BatchTaskResult(null, true, false, true, "进程清理未确认")
                    : new BatchTaskResult(null, true, true, true, "完成"));
            }, null, CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(new[] { "A", "B" }, ran);
        Assert.Equal(2, driver.Switches.Count);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("stopped", batch.State);
        Assert.Contains("停止", batch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsAccountWithoutLoginProfileAndDoesNotRunIt()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var third = await fixture.Database.AddAccountAsync(GameId.StarRail, "C", "", null, "");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        var ran = new List<string>();

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, [fixture.Accounts[0], third], Unified,
            fixture.LoadSettingsAsync, (account, _, _) =>
            {
                ran.Add(account.Name);
                return Task.FromResult(new BatchTaskResult(null, true, true, true, "完成"));
            }, null, CancellationToken.None);

        Assert.Equal(new[] { "A" }, ran);
        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.Succeeded);
        Assert.Equal(1, outcome.Failed);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        var results = await fixture.Database.LoadBatchResultsAsync(batch.Id);
        Assert.Contains(results, r => r.Result == "login_profile_missing");
    }

    [Fact]
    public async Task OnlySelectedAccountsAreSwitched()
    {
        await using var fixture = await Fixture.CreateAsync("A", "C");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, [fixture.Accounts[0], fixture.Accounts[1]],
            Unified, fixture.LoadSettingsAsync,
            (_, _, _) => Task.FromResult(new BatchTaskResult(null, true, true, true, "完成")), null,
            CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(new[] { "153*****57", "139*****11" }, driver.Switches.Select(s => s.MaskedPhone));
    }

    [Fact]
    public async Task FailedInitialLoginPreparationStopsBeforeAnyAccountIsTouched()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver
        {
            EnsureResult = LoginSwitchOutcome.Fail(LoginSwitchStage.FindingWindow, "游戏启动失败"),
        };
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        var ran = false;

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, fixture.Accounts, Unified,
            fixture.LoadSettingsAsync, (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(new BatchTaskResult(null, true, true, true, "完成"));
            }, null, CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.False(ran);
        Assert.Empty(driver.Switches);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("failed", batch.State);
        Assert.Contains("登录界面", batch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserCancellationStopsGameAndPersistsStoppedBatch()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        using var cts = new CancellationTokenSource();

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, fixture.Accounts, Unified,
            fixture.LoadSettingsAsync, (_, _, _) =>
            {
                cts.Cancel();
                return Task.FromException<BatchTaskResult>(new OperationCanceledException(cts.Token));
            }, null, cts.Token);

        Assert.False(outcome.Completed);
        Assert.True(driver.Stops >= 1);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("stopped", batch.State);
        Assert.Contains("用户停止", batch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondAccountReceivesPreviousIdentityAndEvidenceIsStored()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        await orchestrator.ExecuteAsync(GameId.StarRail, fixture.Accounts, Unified, fixture.LoadSettingsAsync,
            (_, _, _) => Task.FromResult(new BatchTaskResult(null, true, true, true, "完成")), null, CancellationToken.None);

        Assert.Null(driver.Switches[0].PreviousUid);
        Assert.Equal("153*****57", driver.Switches[1].PreviousMasked);
        Assert.Equal("100000001", driver.Switches[1].PreviousUid);
        var details = await fixture.SwitchDetailsAsync();
        Assert.Equal(2, details.Count);
        Assert.All(details, d => Assert.Contains("\"Verdict\":\"verified\"", d, StringComparison.Ordinal));
        Assert.Contains(details, d => d.Contains("139*****11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FatalIdentityFailureStopsTheBatchBeforeAnyLaterAccount()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B", "C");
        var driver = new FakeLoginDriver
        {
            SwitchResult = t => t.MaskedPhone == "139*****11"
                ? LoginSwitchOutcome.Fail(LoginSwitchStage.VerifyingSession, "UID 与上一个账号相同") with { Fatal = true }
                : null!,
        };
        var ran = new List<string>();
        var outcome = await new MultiAccountOrchestrator(fixture.Database, driver).ExecuteAsync(GameId.StarRail,
            fixture.Accounts, Unified, fixture.LoadSettingsAsync, (a, _, _) =>
            {
                ran.Add(a.Name);
                return Task.FromResult(new BatchTaskResult(null, true, true, true, "完成"));
            }, null, CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(new[] { "A" }, ran);
        Assert.Equal(2, driver.Switches.Count);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("stopped", batch.State);
        var results = await fixture.Database.LoadBatchResultsAsync(batch.Id);
        Assert.Contains(results, r => r.Result == "identity_uncertain");
    }

    [Fact]
    public async Task AuthenticationChallengeIsRecordedAsNeedsUserAndStops()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver
        {
            SwitchResult = _ => LoginSwitchOutcome.Fail(LoginSwitchStage.WaitingForLogin, "需要验证码") with
                { Fatal = true, NeedsUser = true },
        };
        var progress = new List<BatchRunState>();
        var outcome = await new MultiAccountOrchestrator(fixture.Database, driver).ExecuteAsync(GameId.StarRail,
            fixture.Accounts, Unified, fixture.LoadSettingsAsync,
            (_, _, _) => throw new InvalidOperationException("不应执行"), p => progress.Add(p.State), CancellationToken.None);
        Assert.False(outcome.Completed);
        Assert.Single(driver.Switches);
        Assert.Contains(BatchRunState.ReauthorizationRequired, progress);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Contains(await fixture.Database.LoadBatchResultsAsync(batch.Id), r => r.Result == "needs_user");
    }

    [Fact]
    public async Task RefusedUacStopsTheBatchWithoutRunningTasks()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver
        {
            ThrowOnSwitch = new InputHostUnavailableException("已取消管理员授权", InputHostFailureKind.Refused),
        };
        var session = new FakeHostSession { Launches = 1 };
        var ran = false;
        var outcome = await new MultiAccountOrchestrator(fixture.Database, driver, session).ExecuteAsync(GameId.StarRail,
            fixture.Accounts, Unified, fixture.LoadSettingsAsync, (_, _, _) =>
            {
                ran = true;
                return Task.FromResult(new BatchTaskResult(null, true, true, true, "完成"));
            }, null, CancellationToken.None);

        Assert.False(ran);
        Assert.False(outcome.Completed);
        Assert.Equal(1, session.Begins);
        Assert.Equal(1, session.Ends);
        Assert.Equal(1, outcome.HostLaunches);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("stopped", batch.State);
        Assert.Contains("授权请求 1 次", batch.Detail, StringComparison.Ordinal);
        Assert.Contains(await fixture.Database.LoadBatchResultsAsync(batch.Id), r => r.Result == "uac_refused");
    }

    [Fact]
    public async Task UnconfirmedHostExitIsNeverReportedAsCompleted()
    {
        await using var fixture = await Fixture.CreateAsync("A");
        var session = new FakeHostSession { Launches = 1, ExitConfirmed = false };
        var outcome = await new MultiAccountOrchestrator(fixture.Database, new FakeLoginDriver(), session).ExecuteAsync(
            GameId.StarRail, fixture.Accounts, Unified, fixture.LoadSettingsAsync,
            (_, _, _) => Task.FromResult(new BatchTaskResult(null, true, true, true, "完成")), null, CancellationToken.None);
        Assert.Equal(1, outcome.Succeeded);
        Assert.False(outcome.Completed);
        Assert.False(outcome.HostCleanupConfirmed);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Equal("stopped", batch.State);
        Assert.Contains("残留", batch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalBatchUsesOneHostSessionAndReportsPromptCount()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var session = new FakeHostSession { Launches = 1 };
        var outcome = await new MultiAccountOrchestrator(fixture.Database, new FakeLoginDriver(), session).ExecuteAsync(
            GameId.StarRail, fixture.Accounts, Unified, fixture.LoadSettingsAsync,
            (_, _, _) => Task.FromResult(new BatchTaskResult(null, true, true, true, "完成")), null, CancellationToken.None);
        Assert.True(outcome.Completed);
        Assert.Equal(1, outcome.HostLaunches);
        Assert.Equal(1, session.Begins);
        Assert.Equal(1, session.Ends);
    }

    [Fact]
    public async Task UnrestoredConfigurationBlocksTheNextAccount()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var ran = new List<string>();
        var outcome = await new MultiAccountOrchestrator(fixture.Database, new FakeLoginDriver()).ExecuteAsync(
            GameId.StarRail, fixture.Accounts, Unified, fixture.LoadSettingsAsync, (a, _, _) =>
            {
                ran.Add(a.Name);
                return Task.FromResult(new BatchTaskResult(null, true, true, false, "临时配置未恢复"));
            }, null, CancellationToken.None);
        Assert.Equal(new[] { "A" }, ran);
        Assert.False(outcome.Completed);
        var batch = Assert.Single(await fixture.Database.LoadRecentBatchesAsync());
        Assert.Contains("临时配置未恢复", batch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentModeKeepsEachAccountsOwnSettings()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var seen = new List<string>();
        await new MultiAccountOrchestrator(fixture.Database, new FakeLoginDriver()).ExecuteAsync(GameId.StarRail,
            fixture.Accounts, AccountConfigurationMode.Independent, fixture.LoadSettingsAsync, (_, settings, _) =>
            {
                seen.Add(settings["borrow_enable"]);
                return Task.FromResult(new BatchTaskResult(null, true, true, true, "完成"));
            }, null, CancellationToken.None);
        Assert.Equal(new[] { "true", "false" }, seen);
        // Running the batch never rewrites the stored profiles.
        Assert.Equal("false", (await fixture.LoadSettingsAsync(fixture.Accounts[1], CancellationToken.None))["borrow_enable"]);
    }

    private sealed class FakeHostSession : IInputHostSession
    {
        public int Launches { get; init; }
        public bool ExitConfirmed { get; init; } = true;
        public int Begins { get; private set; }
        public int Ends { get; private set; }
        public InputHostState State => InputHostState.Idle;
        public int LaunchCount => Launches;
        public void BeginBatch() => Begins++;
        public Task<bool> EndBatchAsync(CancellationToken cancellationToken) { Ends++; return Task.FromResult(ExitConfirmed); }
    }

    private sealed class FakeLoginDriver : ILoginSwitchDriver
    {
        public List<LoginTarget> Switches { get; } = [];
        public int Restarts { get; private set; }
        public int Stops { get; private set; }
        public LoginSwitchOutcome EnsureResult { get; init; } =
            LoginSwitchOutcome.Success(LoginSwitchStage.WaitingForLogin, "登录界面就绪");
        public Func<LoginTarget, LoginSwitchOutcome>? SwitchResult { get; init; }
        public Exception? ThrowOnSwitch { get; init; }

        public Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(EnsureResult);
        }

        public Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Switches.Add(target);
            if (ThrowOnSwitch is not null) throw ThrowOnSwitch;
            return Task.FromResult(SwitchResult?.Invoke(target) ??
                LoginSwitchOutcome.Success(LoginSwitchStage.Done, "已进入游戏", "10000000" + Switches.Count) with
                {
                    Evidence = AccountSwitchVerifier.Judge(new AccountSwitchEvidence
                    {
                        TargetMasked = target.MaskedPhone, ConfirmedSelectionMasked = target.MaskedPhone,
                        SelectionConfirmedAt = DateTimeOffset.UnixEpoch, EnterPressedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
                        InGameAt = DateTimeOffset.UnixEpoch.AddSeconds(2), Uid = "10000000" + Switches.Count,
                        PreviousUid = target.PreviousUid,
                    }),
                });
        }

        public Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Restarts++;
            return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.ReturningToLogin, "已返回登录界面"));
        }

        public Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.ReturningToLogin, "已停止游戏"));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;

        private Fixture(string directory, LocalDatabase database, IReadOnlyList<GameAccount> accounts)
        {
            _directory = directory;
            Database = database;
            Accounts = accounts;
        }

        public LocalDatabase Database { get; }
        public IReadOnlyList<GameAccount> Accounts { get; }

        public static async Task<Fixture> CreateAsync(params string[] names)
        {
            var directory = Path.Combine(Path.GetTempPath(), "vivian-orchestrator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new LocalDatabase(Path.Combine(directory, "vivian.db"));
            await database.InitializeAsync();
            var accounts = new List<GameAccount>();
            var phones = new[] { "153*****57", "139*****11", "158*****22" };
            var normalized = new[] { "153****57", "139****11", "158****22" };
            for (var i = 0; i < names.Length; i++)
            {
                var account = await database.AddAccountAsync(GameId.StarRail, names[i], "", null, "");
                accounts.Add(account);
                await database.SaveLoginProfileAsync(account.Id, phones[i], normalized[i], i, 0.95, null, "测试登记");
                var settings = await database.LoadAccountSettingsAsync(account.Id);
                await database.SaveAccountSettingsAsync(settings with
                {
                    Values = new Dictionary<string, string> { ["borrow_enable"] = i == 0 ? "true" : "false" },
                });
            }
            return new Fixture(directory, database, accounts);
        }

        public async Task<IReadOnlyList<string>> SwitchDetailsAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "vivian.db")};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT detail FROM account_switch_history ORDER BY at;";
            var list = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) list.Add(reader.GetString(0));
            return list;
        }

        public async Task<IReadOnlyDictionary<string, string>> LoadSettingsAsync(GameAccount account,
            CancellationToken cancellationToken) =>
            (await Database.LoadAccountSettingsAsync(account.Id, cancellationToken)).Values;

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(_directory);
            return ValueTask.CompletedTask;
        }
    }
}
