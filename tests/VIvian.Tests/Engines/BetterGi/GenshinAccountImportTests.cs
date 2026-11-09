using Microsoft.Data.Sqlite;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinAccountImportTests
{
    [Fact]
    public async Task ReimportKeepsOneGenshinAccountAndLeavesSameMaskInStarRailAlone()
    {
        await using var fixture = new BetterGiFixture();
        var database = new LocalDatabase(Path.Combine(fixture.Root, "import.db"));
        await database.InitializeAsync();
        const string identity = "138****97";
        var starRail = await database.AddAccountAsync(GameId.StarRail, "星铁账号", "", "123456789");
        await database.SaveLoginProfileAsync(starRail.Id, identity, identity, 0, 1, "123456789", "合成测试");
        var service = new GenshinAccountImportService(database);

        var first = await service.RegisterRecognizedAsync(960, 1280, Card(), default);
        var second = await new GenshinAccountImportService(database)
            .RegisterRecognizedAsync(960, 1280, Card("１３８＊＊＊＊９７"), default);

        Assert.True(first.Success);
        Assert.True(first.Created);
        Assert.True(second.Success);
        Assert.False(second.Created);
        Assert.Equal(first.Account!.Id, second.Account!.Id);
        Assert.Equal(GameId.Genshin, first.Account.Game);
        Assert.Null(first.Account.Uid);
        Assert.False(first.Account.AutoRun);
        Assert.Equal(identity, Assert.Single(await database.LoadLoginProfilesAsync(GameId.Genshin)).NormalizedPhone);
        var starRailProfile = Assert.Single(await database.LoadLoginProfilesAsync(GameId.StarRail));
        Assert.Equal(starRail.Id, starRailProfile.AccountId);
        Assert.Equal("123456789", starRailProfile.ObservedUid);
        Assert.Empty((await database.LoadAccountSettingsAsync(first.Account.Id)).Values);
    }

    [Fact]
    public async Task ConcurrentImportsRegisterOnlyOneAccount()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "import.db");
        var database = new LocalDatabase(path);
        await database.InitializeAsync();
        var imports = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            new GenshinAccountImportService(new LocalDatabase(path))
                .RegisterRecognizedAsync(960, 1280, Card(), default))));

        Assert.All(imports, result => Assert.True(result.Success));
        Assert.Single(imports, result => result.Created);
        Assert.Single(imports.Select(result => result.Account!.Id).Distinct());
        Assert.Single(await database.LoadLoginProfilesAsync(GameId.Genshin));
        Assert.Single(await database.LoadAccountsAsync(GameId.Genshin), account => account.LoginState == "recognized");
    }

    [Fact]
    public async Task AmbiguousOrUnmaskedPhotosDoNotCreateProfiles()
    {
        await using var fixture = new BetterGiFixture();
        var database = new LocalDatabase(Path.Combine(fixture.Root, "import.db"));
        await database.InitializeAsync();
        var service = new GenshinAccountImportService(database);

        var ambiguous = await service.RegisterRecognizedAsync(960, 1280,
            [.. Card(), new("139****61", 280, 555, 140, 22)], default);
        var unmasked = await service.RegisterRecognizedAsync(960, 1280, Card("13800000097"), default);
        var challenge = await service.RegisterRecognizedAsync(960, 1280,
            [.. Card(), new("请输入验证码", 420, 540, 125, 20)], default);

        Assert.False(ambiguous.Success);
        Assert.False(unmasked.Success);
        Assert.False(challenge.Success);
        Assert.Empty(await database.LoadLoginProfilesAsync(GameId.Genshin));
    }

    [Fact]
    public async Task ImportLeavesAnUnboundManualAccountSeparateEvenWhenItsNameMatches()
    {
        await using var fixture = new BetterGiFixture();
        var database = new LocalDatabase(Path.Combine(fixture.Root, "import.db"));
        await database.InitializeAsync();
        var manual = await database.AddAccountAsync(GameId.Genshin, "138****97", "", null);

        var result = await new GenshinAccountImportService(database).RegisterRecognizedAsync(960, 1280, Card(), default);

        Assert.True(result.Created);
        Assert.NotEqual(manual.Id, result.Account!.Id);
        Assert.Contains(manual, await database.LoadAccountsAsync(GameId.Genshin));
        Assert.Equal(result.Account.Id, Assert.Single(await database.LoadLoginProfilesAsync(GameId.Genshin)).AccountId);
    }

    [Fact]
    public async Task ExplicitBindingPreservesTasksAndSettingsAndIsIdempotent()
    {
        await using var fixture = new BetterGiFixture();
        var path = Path.Combine(fixture.Root, "import.db");
        var database = new LocalDatabase(path);
        await database.InitializeAsync();
        var selected = await database.AddAccountAsync(GameId.Genshin, "手工档案", "", null);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var seed = connection.CreateCommand();
            seed.CommandText = "UPDATE account_settings SET settings_json=$settings, revision=2 WHERE account_id=$id;";
            seed.Parameters.AddWithValue("$settings", """{"dailyRewards":"true"}""");
            seed.Parameters.AddWithValue("$id", selected.Id.ToString("D"));
            Assert.Equal(1, await seed.ExecuteNonQueryAsync());
        }
        var taskPlan = await database.LoadAccountTaskPlanAsync(selected.Id);
        var savedPlan = await database.SaveAccountTaskPlanAsync(taskPlan with { DailyEnabled = true });
        var service = new GenshinAccountImportService(database);

        var bound = await service.RegisterRecognizedAsync(960, 1280, Card(), default, selected.Id);
        var repeated = await service.RegisterRecognizedAsync(960, 1280, Card("１３８＊＊＊＊９７"), default, selected.Id);

        Assert.True(bound.Success);
        Assert.False(bound.Created);
        Assert.Equal(selected.Id, bound.Account!.Id);
        Assert.Equal("recognized", bound.Account.LoginState);
        Assert.Equal(selected.Name, bound.Account.Name);
        Assert.Null(bound.Account.Uid);
        Assert.True(repeated.Success);
        Assert.False(repeated.Created);
        Assert.Equal(bound.Account, repeated.Account);
        Assert.Equal(selected.Id, Assert.Single(await database.LoadLoginProfilesAsync(GameId.Genshin)).AccountId);
        var currentSettings = await database.LoadAccountSettingsAsync(selected.Id);
        Assert.Equal(2, currentSettings.Revision);
        Assert.Equal("true", currentSettings.Values["dailyRewards"]);
        Assert.Equal(savedPlan, await database.LoadAccountTaskPlanAsync(selected.Id));
    }

    [Fact]
    public async Task ExplicitBindingRefusesConflictsAndIneligibleTargets()
    {
        await using var fixture = new BetterGiFixture();
        var database = new LocalDatabase(Path.Combine(fixture.Root, "import.db"));
        await database.InitializeAsync();
        var legacy = Assert.Single(await database.LoadAccountsAsync(GameId.Genshin));
        var first = await database.AddAccountAsync(GameId.Genshin, "已有绑定", "", null);
        var other = await database.AddAccountAsync(GameId.Genshin, "待绑定", "", null);
        var starRail = await database.AddAccountAsync(GameId.StarRail, "星铁档案", "", null);
        var zenless = await database.AddAccountAsync(GameId.ZenlessZoneZero, "绝区零档案", "", null);
        var disabled = await database.AddAccountAsync(GameId.Genshin, "停用档案", "", null);
        await database.SaveAccountAsync(disabled with { Enabled = false });
        var service = new GenshinAccountImportService(database);
        await database.SaveLoginProfileAsync(first.Id, "138****97", "138****97", 0, 1, null, "合成测试");

        var usedByAnother = await service.RegisterRecognizedAsync(960, 1280, Card(), default, other.Id);
        var differentIdentity = await service.RegisterRecognizedAsync(960, 1280, Card("139****61"), default, first.Id);
        Assert.False(usedByAnother.Success);
        Assert.False(differentIdentity.Success);
        foreach (var id in new[] { starRail.Id, zenless.Id, legacy.Id, disabled.Id, Guid.NewGuid(), Guid.Empty })
            Assert.False((await service.RegisterRecognizedAsync(960, 1280, Card("139****61"), default, id)).Success);

        var profile = Assert.Single(await database.LoadLoginProfilesAsync());
        Assert.Equal(first.Id, profile.AccountId);
        Assert.Equal("138****97", profile.NormalizedPhone);
    }

    [Fact]
    public async Task MissingImageReturnsFailureWithoutExposingThePath()
    {
        await using var fixture = new BetterGiFixture();
        var database = new LocalDatabase(Path.Combine(fixture.Root, "import.db"));
        await database.InitializeAsync();
        const string privateName = "personal-file-name.png";

        var result = await new GenshinAccountImportService(database)
            .ImportAndRegisterAsync(Path.Combine(fixture.Root, privateName));

        Assert.False(result.Success);
        Assert.Null(result.Account);
        Assert.DoesNotContain(privateName, result.Detail);
    }

    private static RecognizedText[] Card(string identity = "138****97") =>
    [
        new("HoYo", 452, 360, 136, 57),
        new(identity, 280, 500, 140, 22),
        new("进入游戏", 428, 656, 115, 28),
        new("录其地账号", 422, 752, 130, 20),
    ];
}
