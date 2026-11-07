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
    public async Task RunsTwoAccountsAndUsesFirstSnapshotInUnifiedMode()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver();
        var orchestrator = new MultiAccountOrchestrator(fixture.Database, driver);
        var calls = new List<(string Name, string Value)>();

        var outcome = await orchestrator.ExecuteAsync(GameId.StarRail, [fixture.Accounts[0], fixture.Accounts[1]],
            Unified, fixture.LoadSettingsAsync, (account, settings, _) =>
            {
                calls.Add((account.Name, settings["borrow_enable"]));
                return Task.FromResult(new BatchTaskResult(Guid.NewGuid(), true, true, true, "日常完成"));
            }, null, CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(new[] { ("A", "true"), ("B", "true") }, calls);
        Assert.Equal(new[] { "153****57", "139****11" }, driver.Switches.Select(s => s.NormalizedPhone));
        Assert.Equal(3, driver.Restarts);
        Assert.Equal(0, driver.Stops);

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
    public async Task FailedRestartToLoginStopsBeforeAnyAccountIsTouched()
    {
        await using var fixture = await Fixture.CreateAsync("A", "B");
        var driver = new FakeLoginDriver
        {
            RestartResult = LoginSwitchOutcome.Fail(LoginSwitchStage.ReturningToLogin, "游戏启动失败"),
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

    private sealed class FakeLoginDriver : ILoginSwitchDriver
    {
        public List<LoginTarget> Switches { get; } = [];
        public int Restarts { get; private set; }
        public int Stops { get; private set; }
        public LoginSwitchOutcome RestartResult { get; init; } =
            LoginSwitchOutcome.Success(LoginSwitchStage.ReturningToLogin, "已返回登录界面");

        public Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.WaitingForLogin, "登录界面就绪"));
        }

        public Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Switches.Add(target);
            return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "已进入游戏", null));
        }

        public Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Restarts++;
            return Task.FromResult(RestartResult);
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
