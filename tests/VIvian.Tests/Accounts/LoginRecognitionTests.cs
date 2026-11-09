using VIvian.Core.Accounts;
using Xunit;

namespace VIvian.Tests.Accounts;

public sealed class LoginRecognitionTests
{
    private static RecognizedText Line(string text, double x, double y, double width = 140, double height = 24) =>
        new(text, x, y, width, height);

    [Fact]
    public void ParsesCompleteMaskedPhoneAndDetectsLoginDialog()
    {
        var lines = new[]
        {
            Line("153*****57", 820, 490, 180),
            Line("进入游戏", 820, 620, 150),
            Line("登录其他账号", 850, 680, 140),
        };

        var result = LoginScreenAnalyzer.Analyze(1920, 1080, lines);

        Assert.True(result.Success);
        Assert.Equal(LoginScanFailure.None, result.Failure);
        var observation = Assert.IsType<LoginScreenObservation>(result.Observation);
        var account = Assert.Single(observation.Accounts);
        Assert.Equal("153*****57", account.MaskedPhone);
        Assert.Equal("153****57", account.NormalizedPhone);
        Assert.Equal(0, account.RowOrder);
        Assert.Equal(0.82, account.Confidence, 3);
        Assert.True(observation.LoginDialogVisible);
        Assert.False(observation.AccountListExpanded);
        Assert.Equal(0.87, observation.Confidence, 3);
    }

    [Fact]
    public void ReconstructsMaskedPhoneWhenOcrDropsAsterisks()
    {
        var lines = new[]
        {
            Line("153", 820, 490, 40),
            Line("57", 880, 490, 24),
            Line("进入游戏", 820, 620, 150),
            Line("登录其他账号", 850, 680, 140),
        };

        var observation = Assert.IsType<LoginScreenObservation>(
            LoginScreenAnalyzer.Analyze(1920, 1080, lines).Observation);
        var account = Assert.Single(observation.Accounts);

        Assert.Equal("153****57", account.NormalizedPhone);
        Assert.Equal(0.8, account.Confidence, 3);
        Assert.Equal(0.85, observation.Confidence, 3);
    }

    [Fact]
    public void NormalizesFullWidthDigitsAndMaskCharacters()
    {
        var lines = new[]
        {
            Line("１５３＊＊＊＊＊５７", 820, 490, 180),
            Line("最近登录", 820, 540, 100),
            Line("进入游戏", 820, 620, 150),
        };

        var observation = Assert.IsType<LoginScreenObservation>(
            LoginScreenAnalyzer.Analyze(1920, 1080, lines).Observation);
        var account = Assert.Single(observation.Accounts);

        Assert.Equal("153****57", account.NormalizedPhone);
        Assert.True(observation.AccountListExpanded);
    }

    [Fact]
    public void OrdersMultipleAccountsByVerticalPosition()
    {
        var lines = new[]
        {
            Line("139*****11", 820, 700, 180),
            Line("158*****22", 820, 600, 180),
            Line("进入游戏", 820, 800, 150),
        };

        var observation = Assert.IsType<LoginScreenObservation>(
            LoginScreenAnalyzer.Analyze(1920, 1080, lines).Observation);

        Assert.Equal(2, observation.Accounts.Count);
        Assert.Equal("158****22", observation.Accounts[0].NormalizedPhone);
        Assert.Equal("139****11", observation.Accounts[1].NormalizedPhone);
        Assert.Equal(0, observation.Accounts[0].RowOrder);
        Assert.Equal(1, observation.Accounts[1].RowOrder);
        Assert.True(observation.AccountListExpanded);
        Assert.Equal(2, observation.Accounts.Count);
    }

    [Fact]
    public void MaskedNumberWithoutLoginControlsIsNotTreatedAsLoginScreen()
    {
        var result = LoginScreenAnalyzer.Analyze(1920, 1080, new[] { Line("153*****57", 800, 400) });

        Assert.False(result.Success);
        Assert.Equal(LoginScanFailure.LoginScreenNotFound, result.Failure);
        Assert.Contains("脱敏手机号", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOcrOutputReportsOcrFailed()
    {
        var result = LoginScreenAnalyzer.Analyze(1920, 1080, Array.Empty<RecognizedText>());

        Assert.False(result.Success);
        Assert.Equal(LoginScanFailure.OcrFailed, result.Failure);
    }

    [Fact]
    public void ResolvesOnlyAnUnambiguousSavedAccount()
    {
        var accounts = new[]
        {
            new LoginAccountObservation("153*****57", "153****57", 0, 820, 490, 180, 24, 0.95, ""),
            new LoginAccountObservation("153*****57", "153****57", 1, 820, 540, 180, 24, 0.95, ""),
        };

        var ambiguous = AccountSelectionResolver.Resolve(accounts, "153****57", 2);
        Assert.False(ambiguous.Success);
        Assert.Equal(AccountSelectionFailure.Ambiguous, ambiguous.Failure);

        var byOrder = AccountSelectionResolver.Resolve(accounts, "153****57", 1);
        Assert.False(byOrder.Success);
        Assert.Equal(AccountSelectionFailure.Ambiguous, byOrder.Failure);

        var missing = AccountSelectionResolver.Resolve(accounts, "139****11", 0);
        Assert.False(missing.Success);
        Assert.Equal(AccountSelectionFailure.NotFound, missing.Failure);
    }

    [Fact]
    public void UnifiedModeInheritsFirstSnapshotWithoutMutatingIt()
    {
        var first = new Dictionary<string, string> { ["borrow_enable"] = "true" };
        var second = new Dictionary<string, string> { ["borrow_enable"] = "false" };

        var unified = BatchConfigurationResolver.Resolve(first, second, AccountConfigurationMode.Unified);
        var independent = BatchConfigurationResolver.Resolve(first, second, AccountConfigurationMode.Independent);

        Assert.Equal("true", unified["borrow_enable"]);
        Assert.Equal("false", independent["borrow_enable"]);
        Assert.NotSame(first, unified);
        Assert.Equal("true", first["borrow_enable"]);
    }

    [Fact]
    public void SwitchPolicyRejectsUnsafeRetryAndTimeoutValues()
    {
        var policy = new SwitchPolicy();
        Assert.Equal(2, policy.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(90), policy.LoginScreen);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwitchPolicy(MaxAttempts: 0).Attempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwitchPolicy(MaxAttempts: 6).Attempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwitchPolicy(StepTimeout: TimeSpan.Zero).Step);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwitchPolicy(GameStartTimeout: TimeSpan.FromMinutes(16)).GameStart);
    }

    [Fact]
    public void ReadsUidFromLabelAndSeparateNumber()
    {
        var inline = GameUidParser.Find(new[] { Line("UID: 123456789", 100, 100) });
        Assert.Equal("123456789", inline);

        var split = GameUidParser.Find(new[]
        {
            Line("UID", 100, 100, 30, 14),
            Line("101234567", 150, 102, 80, 16),
        });
        Assert.Equal("101234567", split);
    }

    [Fact]
    public void UidTextIsNotMistakenForTheLoginScreen()
    {
        var login = new[] { Line("点击进入", 900, 700) };
        Assert.False(GameUidParser.LooksInGame(login));
        Assert.False(GameUidParser.LooksInGame([Line("UID: 123456789", 110, 1040)]));
        Assert.False(GameUidParser.LooksInGame([Line("星魂", 900, 870),
            Line("跃迁会遇到许多角色", 900, 930), Line("UID: 123456789", 110, 1040)]));

        var inGame = new[] { Line("开拓等级 65", 200, 300), Line("星琼 120", 200, 340) };
        Assert.True(GameUidParser.LooksInGame(inGame));
    }
}
