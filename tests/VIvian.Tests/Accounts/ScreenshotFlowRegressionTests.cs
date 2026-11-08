using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using Xunit;
using static VIvian.Tests.Accounts.LoginFlowTests;

namespace VIvian.Tests.Accounts;

// Transcribed reference screenshots, not a claim of live-game validation.
public sealed class ScreenshotFlowRegressionTests
{
    [Fact]
    public void RetentionConfirmationRequiresVisibleSelectedMarker()
    {
        RecognizedText[] lines = [T("退出登录", 300, 100), T("退出并保留登录记录", 300, 200, 180, 20),
            T("取消", 240, 260), T("退出", 420, 260)];
        var pixels = Enumerable.Repeat((byte)245, 600 * 400 * 4).ToArray();
        var frame = new CapturedFrame(pixels, 600, 400, "test");
        Assert.False(LoginRetentionDetector.IsSelected(frame, lines));
        for (var y = 193; y <= 207; y++)
        for (var x = 186; x <= 200; x++)
        {
            var p = (y * 600 + x) * 4;
            pixels[p] = 100; pixels[p + 1] = 205; pixels[p + 2] = 255;
        }
        Assert.True(LoginRetentionDetector.IsSelected(frame, lines));
        var o = LoginStateClassifier.Classify(lines, preserveLoginSelected: true);
        Assert.Equal("退出", new LoginFlowSession(LoginGoal.AccountSelectable).Decide(o).Target?.Text);
    }

    [Fact]
    public void KnownLoadingDoesNotConsumeUnknownScreenBudget()
    {
        var session = new LoginFlowSession(LoginGoal.InWorld);
        var o = LoginStateClassifier.Classify([T("正在加载", 960, 500)]);
        for (var i = 0; i < 20; i++) Assert.Equal(LoginActionKind.Wait, session.Decide(o).Kind);
    }

    [Fact]
    public void Screenshot3LogsOutButWorldGoalUsesStartGame()
    {
        var o = LoginStateClassifier.Classify([T("开始游戏", 960, 880), T("星穹列车", 960, 950), T("登出", 1830, 350), T("退出", 1830, 450)]);
        Assert.Equal(LoginScreenState.Title, o.State);
        Assert.Equal("登出", new LoginFlowSession(LoginGoal.AccountSelectable).Decide(o).Target?.Text);
        Assert.Equal("开始游戏", new LoginFlowSession(LoginGoal.InWorld).Decide(o).Target?.Text);
    }

    [Fact]
    public void Screenshot4SelectsPreserveBeforeConfirmingLogout()
    {
        var o = LoginStateClassifier.Classify([T("退出登录", 960, 450), T("退出并清除登录记录", 900, 520),
            T("退出并保留登录记录", 900, 615), T("取消", 810, 725), T("退出", 1090, 725), T("开始游戏", 960, 880)]);
        Assert.Equal(LoginScreenState.LogoutConfirm, o.State);
        Assert.Equal("退出并保留登录记录", new LoginFlowSession(LoginGoal.AccountSelectable).Decide(o).Target?.Text);
    }

    [Fact]
    public void Screenshot8WelcomeBannerIsTitleNotLoginCard()
    {
        var o = LoginStateClassifier.Classify([T("152******39，欢迎进入游戏", 960, 230, 400),
            T("开始游戏", 960, 880), T("登出", 1830, 350)], true);
        Assert.Equal(LoginScreenState.Title, o.State);
    }

    [Fact]
    public void DuplicateMaskedIdentitiesCannotBeResolvedByHistoricalRowOrder()
    {
        var rows = MaskedPhoneParser.FindAll([T("153******57", 900, 500), T("153******57", 900, 600)]);
        var result = AccountSelectionResolver.Resolve(rows, "153****57", 1);
        Assert.False(result.Success);
        Assert.Equal(AccountSelectionFailure.Ambiguous, result.Failure);
    }

    [Fact]
    public void Screenshot6ExcludesHeaderButKeepsBothSavedRows()
    {
        var result = LoginScreenAnalyzer.Analyze(1920, 1080, [T("miHoYo", 960, 450),
            T("153******57", 840, 540), T("153******57", 840, 610), T("152******39", 840, 700), T("登录其他账号", 960, 795)],
            new ScreenRegion(632, 371, 666, 454),
            new LoginAccountObservation("153******57", "153****57", 0, 760, 525, 160, 30, .9, ""));
        Assert.True(result.Success);
        Assert.Equal(2, result.Observation!.Accounts.Count);
        Assert.True(result.Observation.AccountListExpanded);
        Assert.True(result.Observation.Accounts[0].CenterY > 580);
    }

    [Fact]
    public void MissingComboHeaderCannotHideAmbiguousRealRows()
    {
        var lines = new[] { T("153******57", 840, 610), T("153******57", 840, 700),
            T("152******39", 840, 790), T("登录其他账号", 960, 880) };
        var result = LoginScreenAnalyzer.Analyze(1920, 1080, lines, new ScreenRegion(632, 371, 666, 540));
        Assert.Equal(3, result.Observation!.Accounts.Count);
        Assert.Equal(AccountSelectionFailure.Ambiguous,
            AccountSelectionResolver.Resolve(result.Observation.Accounts, "153****57", 0).Failure);
    }
}
