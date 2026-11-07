using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using Xunit;

namespace VIvian.Tests.Accounts;

public sealed class AccountRecognitionServiceTests
{
    [Fact]
    public async Task ScanCreatesProfilesAndSecondScanDoesNotDuplicateThem()
    {
        await using var fixture = await Fixture.CreateAsync(SampleView(
            ("153*****57", "153****57", 0),
            ("139*****11", "139****11", 1)));

        var first = await fixture.Service.ScanAndRegisterAsync(CancellationToken.None);

        Assert.True(first.Success);
        Assert.Equal(2, first.Registered.Count);
        Assert.Empty(first.Warnings);
        Assert.Contains("已登记 2", first.Detail, StringComparison.Ordinal);
        Assert.Equal(2, await fixture.StarRailAccountCountAsync());
        Assert.Equal(2, (await fixture.Database.LoadLoginProfilesAsync(GameId.StarRail)).Count);

        var second = await fixture.Service.ScanAndRegisterAsync(CancellationToken.None);

        Assert.True(second.Success);
        Assert.Equal(2, second.Registered.Count);
        Assert.Equal(2, await fixture.StarRailAccountCountAsync());
        Assert.Equal(2, (await fixture.Database.LoadScanSessionsAsync()).Count);
    }

    [Fact]
    public async Task DuplicateMaskedNumberInOneScanCreatesOnlyOneProfile()
    {
        await using var fixture = await Fixture.CreateAsync(SampleView(
            ("153*****57", "153****57", 0),
            ("153*****57", "153****57", 1)));

        var result = await fixture.Service.ScanAndRegisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Registered);
        Assert.Single(result.Warnings);
        Assert.Contains("未重复建档", result.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(1, await fixture.StarRailAccountCountAsync());
    }

    [Fact]
    public async Task AmbiguousExistingProfilesAreReportedInsteadOfGuessed()
    {
        await using var fixture = await Fixture.CreateAsync(SampleView(("153*****57", "153****57", 0)));
        var one = await fixture.Database.AddAccountAsync(GameId.StarRail, "档案一", "", null, "");
        var two = await fixture.Database.AddAccountAsync(GameId.StarRail, "档案二", "", null, "");
        await fixture.Database.SaveLoginProfileAsync(one.Id, "153*****57", "153****57", 0, 0.9, null, "");
        await fixture.Database.SaveLoginProfileAsync(two.Id, "153*****57", "153****57", 1, 0.9, null, "");

        var result = await fixture.Service.ScanAndRegisterAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Registered);
        Assert.Contains(result.Warnings, warning => warning.Contains("多个档案", StringComparison.Ordinal));
        var accounts = await fixture.Database.LoadAccountsAsync(GameId.StarRail);
        Assert.Equal(2, accounts.Count(a => a.Name.StartsWith("档案", StringComparison.Ordinal)));
        Assert.Equal(0, await fixture.StarRailAccountCountAsync());
    }

    [Fact]
    public async Task FailedScanDoesNotCreateAnAccountOrSession()
    {
        var failure = new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.LoginScreenNotFound,
            "没有登录界面"), null, null, null);
        await using var fixture = await Fixture.CreateAsync(failure);

        var result = await fixture.Service.ScanAndRegisterAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("没有登录界面", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.StarRailAccountCountAsync());
        Assert.Empty(await fixture.Database.LoadScanSessionsAsync());
        Assert.Equal(1, fixture.Source.ScanCount);
    }

    [Fact]
    public async Task ImportUsesTheProvidedFrameAndSourceLabel()
    {
        await using var fixture = await Fixture.CreateAsync(SampleView(("153*****57", "153****57", 0)));
        var imported = 0;
        var service = new AccountRecognitionService(fixture.Database, fixture.Source, (path, _) =>
        {
            imported++;
            Assert.EndsWith("login.png", path, StringComparison.Ordinal);
            return Task.FromResult(fixture.Source.View);
        });

        var result = await service.ImportAndRegisterAsync("login.png", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Registered);
        Assert.Equal(1, imported);
        Assert.Equal(0, fixture.Source.ScanCount);
        var session = Assert.Single(await fixture.Database.LoadScanSessionsAsync());
        Assert.Equal("import", session.Source);
    }

    private static LoginScreenView SampleView(params (string Masked, string Normalized, int Row)[] accounts)
    {
        var observations = accounts.Select((entry, index) => new LoginAccountObservation(entry.Masked,
            entry.Normalized, entry.Row, 820, 470 + index * 50, 180, 24, 0.9, "")).ToArray();
        var observation = new LoginScreenObservation(1920, 1080, true, accounts.Length > 1, observations,
            0.9, "测试登录界面");
        var lines = accounts.Select((entry, index) =>
            new RecognizedText(entry.Masked, 820, 470 + index * 50, 180, 24)).Append(
            new RecognizedText("进入游戏", 820, 700, 140, 24)).ToArray();
        return new LoginScreenView(new LoginScanResult(true, LoginScanFailure.None, observation.Detail,
            observation, lines.Select(l => l.Text).ToArray()), null, null,
            new LoginPanelRect(630, 326, 666, 422)) { Lines = lines };
    }

    private sealed class FakeSource(LoginScreenView view) : ILoginScreenSource
    {
        public LoginScreenView View { get; set; } = view;
        public int ScanCount { get; private set; }

        public Task<LoginScreenView> ScanAsync(CancellationToken cancellationToken)
        {
            ScanCount++;
            return Task.FromResult(View);
        }

        public Task<LoginScreenView> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(View);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;

        private Fixture(string directory, LocalDatabase database, FakeSource source, AccountRecognitionService service)
        {
            _directory = directory;
            Database = database;
            Source = source;
            Service = service;
        }

        public LocalDatabase Database { get; }
        public FakeSource Source { get; }
        public AccountRecognitionService Service { get; }

        public static async Task<Fixture> CreateAsync(LoginScreenView view)
        {
            var directory = Path.Combine(Path.GetTempPath(), "vivian-recognition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = new LocalDatabase(Path.Combine(directory, "vivian.db"));
            await database.InitializeAsync();
            var source = new FakeSource(view);
            var service = new AccountRecognitionService(database, source,
                (_, _) => Task.FromResult(source.View));
            return new Fixture(directory, database, source, service);
        }

        public async Task<int> StarRailAccountCountAsync() =>
            (await Database.LoadAccountsAsync(GameId.StarRail)).Count(a => a.Name.StartsWith("星铁 ",
                StringComparison.Ordinal));

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(_directory);
            return ValueTask.CompletedTask;
        }
    }
}
