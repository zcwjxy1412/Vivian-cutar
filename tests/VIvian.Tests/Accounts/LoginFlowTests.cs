using VIvian.Core.Accounts;
using Xunit;

namespace VIvian.Tests.Accounts;

/// <summary>Pure state-machine tests. All screens here are simulated OCR output, not real game frames.</summary>
public sealed class LoginFlowTests
{
    internal static RecognizedText T(string text, double x, double y, double w = 160, double h = 30) =>
        new(text, x - w / 2, y - h / 2, w, h);

    internal static readonly RecognizedText[] TitleWithLogout = [T("点击进入", 960, 900), T("退出登录", 1800, 60, 120)];
    internal static readonly RecognizedText[] TitleWithoutLogout = [T("点击进入", 960, 900), T("版本 3.7", 100, 1040)];
    internal static readonly RecognizedText[] LogoutConfirm = [T("确定退出登录？", 960, 500, 300), T("取消", 800, 650, 80), T("确定", 1100, 650, 80)];
    internal static readonly RecognizedText[] CardA = [T("153*****57", 960, 400, 200), T("进入游戏", 960, 600), T("登录其他账号", 960, 700)];
    internal static readonly RecognizedText[] CardB = [T("139*****11", 960, 400, 200), T("进入游戏", 960, 600), T("登录其他账号", 960, 700)];
    internal static readonly RecognizedText[] ListAB = [T("153*****57", 960, 400, 200), T("139*****11", 960, 460, 200), T("进入游戏", 960, 600)];
    internal static readonly RecognizedText[] World = [T("开拓等级 70", 200, 80), T("UID 100000002", 1700, 1050)];
    internal static readonly RecognizedText[] Captcha = [T("请完成安全验证", 960, 400, 300), T("拖动滑块完成拼图", 960, 500, 300)];
    internal static readonly RecognizedText[] Agreement = [T("用户协议与隐私政策", 960, 300, 300), T("同意", 1100, 700, 80), T("不同意", 800, 700, 80)];
    internal static readonly RecognizedText[] Garbage = [T("§§ ## ~~", 500, 500)];

    [Theory]
    [MemberData(nameof(Screens))]
    public void ClassifiesKnownScreens(string name, LoginScreenState expected)
    {
        var lines = name switch
        {
            "title" => TitleWithLogout, "title-no-logout" => TitleWithoutLogout, "confirm" => LogoutConfirm,
            "cardA" => CardA, "list" => ListAB, "world" => World, "captcha" => Captcha, "agreement" => Agreement,
            "garbage" => Garbage, "empty" => [], "loading" => [T("加载中 45%", 960, 900)],
            "notice" => [T("服务器维护公告", 960, 300, 300), T("我知道了", 960, 700)],
            "password" => [T("手机号", 700, 400), T("账号密码登录", 960, 300), T("获取验证码", 1100, 400)],
            _ => throw new ArgumentException(name),
        };
        Assert.Equal(expected, LoginStateClassifier.Classify(lines).State);
    }

    public static TheoryData<string, LoginScreenState> Screens() => new()
    {
        { "title", LoginScreenState.Title }, { "title-no-logout", LoginScreenState.Title },
        { "confirm", LoginScreenState.LogoutConfirm }, { "cardA", LoginScreenState.LoginCard },
        { "list", LoginScreenState.AccountList }, { "world", LoginScreenState.InGame },
        { "captcha", LoginScreenState.AuthChallenge }, { "agreement", LoginScreenState.AgreementRequired },
        { "garbage", LoginScreenState.Unknown }, { "empty", LoginScreenState.Unknown },
        { "loading", LoginScreenState.Loading }, { "notice", LoginScreenState.Notice },
        { "password", LoginScreenState.AuthChallenge },
    };

    [Fact]
    public void SelectionGoalLogsOutFromTitleInsteadOfEnteringAsThePreviousAccount()
    {
        var session = new LoginFlowSession(LoginGoal.AccountSelectable);
        var action = session.Decide(LoginStateClassifier.Classify(TitleWithLogout));
        Assert.Equal(LoginActionKind.Click, action.Kind);
        Assert.Equal("退出登录", action.Target!.Text);
    }

    [Fact]
    public void TitleWithoutLogoutControlFailsWithoutClickingEnter()
    {
        var action = new LoginFlowSession(LoginGoal.AccountSelectable).Decide(LoginStateClassifier.Classify(TitleWithoutLogout));
        Assert.Equal(LoginActionKind.Fail, action.Kind);
        Assert.Contains("不会点击", action.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoutConfirmationPressesTheConfirmButton()
    {
        var action = new LoginFlowSession(LoginGoal.AccountSelectable).Decide(LoginStateClassifier.Classify(LogoutConfirm));
        Assert.Equal(LoginActionKind.Click, action.Kind);
        Assert.Equal("确定", action.Target!.Text);
    }

    [Fact]
    public void UnknownScreensAreOnlyWaitedOnThenFail()
    {
        var session = new LoginFlowSession(LoginGoal.AccountSelectable, new LoginFlowBudget(MaxUnknownStreak: 3));
        var unknown = LoginStateClassifier.Classify(Garbage);
        for (var i = 0; i < 3; i++) Assert.Equal(LoginActionKind.Wait, session.Decide(unknown).Kind);
        var final = session.Decide(unknown);
        Assert.Equal(LoginActionKind.Fail, final.Kind);
        Assert.Contains("盲点", final.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenThatDoesNotReactToClicksStopsAfterTheRetryLimit()
    {
        var session = new LoginFlowSession(LoginGoal.AccountSelectable, new LoginFlowBudget(MaxClicksPerState: 2));
        var title = LoginStateClassifier.Classify(TitleWithLogout);
        Assert.Equal(LoginActionKind.Click, session.Decide(title).Kind);
        Assert.Equal(LoginActionKind.Click, session.Decide(title).Kind);
        Assert.Equal(LoginActionKind.Fail, session.Decide(title).Kind);
    }

    [Fact]
    public void TotalStepBudgetEndsAnyLoop()
    {
        var session = new LoginFlowSession(LoginGoal.InWorld, new LoginFlowBudget(MaxSteps: 5));
        var card = LoginStateClassifier.Classify(CardA);
        for (var i = 0; i < 5; i++) Assert.Equal(LoginActionKind.Wait, session.Decide(card).Kind);
        Assert.Equal(LoginActionKind.Fail, session.Decide(card).Kind);
    }

    [Theory]
    [InlineData(LoginGoal.AccountSelectable)]
    [InlineData(LoginGoal.InWorld)]
    public void AuthenticationAndConsentAreLeftToTheUser(LoginGoal goal)
    {
        Assert.Equal(LoginActionKind.NeedUser, new LoginFlowSession(goal).Decide(LoginStateClassifier.Classify(Captcha)).Kind);
        Assert.Equal(LoginActionKind.NeedUser, new LoginFlowSession(goal).Decide(LoginStateClassifier.Classify(Agreement)).Kind);
    }

    [Fact]
    public void InWorldDuringSelectionRestartsOnceThenFails()
    {
        var session = new LoginFlowSession(LoginGoal.AccountSelectable);
        var world = LoginStateClassifier.Classify(World);
        Assert.Equal(LoginActionKind.RestartGame, session.Decide(world).Kind);
        Assert.Equal(LoginActionKind.Fail, session.Decide(world).Kind);
    }

    [Fact]
    public void WorldGoalPassesTitleAndFinishesInGame()
    {
        var session = new LoginFlowSession(LoginGoal.InWorld);
        var click = session.Decide(LoginStateClassifier.Classify(TitleWithLogout));
        Assert.Equal(LoginActionKind.Click, click.Kind);
        Assert.Equal("点击进入", click.Target!.Text);
        Assert.Equal(LoginActionKind.Done, session.Decide(LoginStateClassifier.Classify(World)).Kind);
    }

    [Fact]
    public void WorldGoalNeverClicksTheLoginCardAgain()
    {
        var session = new LoginFlowSession(LoginGoal.InWorld);
        Assert.Equal(LoginActionKind.Wait, session.Decide(LoginStateClassifier.Classify(CardB)).Kind);
    }

    // ---- identity evidence --------------------------------------------------------------------------------------

    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static AccountSwitchEvidence Good() => new()
    {
        TargetMasked = "139*****11", PreviousMasked = "153*****57", ConfirmedSelectionMasked = "139*****11",
        SelectionConfirmedAt = T0, EnterPressedAt = T0.AddSeconds(1), TitlePassedAt = T0.AddSeconds(20),
        InGameAt = T0.AddSeconds(40), Uid = "100000002", PreviousUid = "100000001",
    };

    [Fact]
    public void ConfirmedSelectionAndContinuousSessionIsVerified()
    {
        var judged = AccountSwitchVerifier.Judge(Good());
        Assert.Equal("verified", judged.Verdict);
        Assert.DoesNotContain("100000002", judged.ToJson(), StringComparison.Ordinal);
        Assert.Contains("10*****02", judged.VerdictDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void InGameWithoutConfirmedSelectionIsNotProofOfTheTargetAccount()
    {
        Assert.Equal("mismatch", AccountSwitchVerifier.Judge(Good() with { ConfirmedSelectionMasked = null }).Verdict);
        Assert.Equal("mismatch", AccountSwitchVerifier.Judge(Good() with { ConfirmedSelectionMasked = "153*****57" }).Verdict);
    }

    [Fact]
    public void SameUidAsPreviousAccountMeansTheSwitchDidNotHappen()
    {
        var judged = AccountSwitchVerifier.Judge(Good() with { Uid = "100000001" });
        Assert.Equal("mismatch", judged.Verdict);
        Assert.Contains("上一个账号相同", judged.VerdictDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileUidMismatchRestartAndOrderingAreAllRejected()
    {
        Assert.Equal("mismatch", AccountSwitchVerifier.Judge(Good() with { ExpectedUid = "100000009" }).Verdict);
        Assert.Equal("mismatch", AccountSwitchVerifier.Judge(Good() with { RestartAfterSelection = true }).Verdict);
        Assert.Equal("mismatch", AccountSwitchVerifier.Judge(Good() with { EnterPressedAt = T0.AddSeconds(-1) }).Verdict);
        Assert.Equal("mismatch", AccountSwitchVerifier.Judge(Good() with { InGameAt = null }).Verdict);
    }

    [Fact]
    public void MissingUidIsStillVerifiedButSaysSo()
    {
        var judged = AccountSwitchVerifier.Judge(Good() with { Uid = null });
        Assert.Equal("verified", judged.Verdict);
        Assert.Contains("未读到 UID", judged.VerdictDetail, StringComparison.Ordinal);
    }

    // ---- capture ownership -------------------------------------------------------------------------------------

    [Fact]
    public void OccludedBitBltFrameIsRejectedAndNamesTheOccluder()
    {
        var (accept, failure, detail) = CaptureVerdict.Evaluate(new CaptureCheck(true, true, false, "bitblt", 9, 6, false, "VIvian 自身窗口"));
        Assert.False(accept);
        Assert.Equal(LoginScanFailure.WindowOccluded, failure);
        Assert.Contains("VIvian 自身窗口", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureVerdictCoversMinimisedWrongProcessBlankAndMissingWindow()
    {
        Assert.Equal(LoginScanFailure.WindowMinimized, CaptureVerdict.Evaluate(new(true, true, true, "none", 0, 0, false)).Failure);
        Assert.Equal(LoginScanFailure.CapturedWrongWindow, CaptureVerdict.Evaluate(new(true, false, false, "none", 0, 0, false)).Failure);
        Assert.Equal(LoginScanFailure.CaptureFailed, CaptureVerdict.Evaluate(new(true, true, false, "bitblt", 9, 9, true)).Failure);
        Assert.Equal(LoginScanFailure.WindowNotFound, CaptureVerdict.Evaluate(new(false, false, false, "none", 0, 0, false)).Failure);
    }

    [Fact]
    public void FullyOwnedOrRenderedFramesAreAccepted()
    {
        Assert.True(CaptureVerdict.Evaluate(new(true, true, false, "bitblt", 9, 9, false)).Accept);
        Assert.True(CaptureVerdict.Evaluate(new(true, true, false, "print", 0, 0, false)).Accept);
    }

    [Fact]
    public void ScanEvidenceDescribesWindowAndMaskedAccountsOnly()
    {
        var accounts = MaskedPhoneParser.FindAll(CardA);
        var evidence = new ScanEvidence(0x1234, 4321, "StarRail", 1920, 1080, T0,
            new CaptureCheck(true, true, false, "bitblt", 9, 9, false), accounts);
        var text = evidence.Describe();
        Assert.Contains("pid 4321", text, StringComparison.Ordinal);
        Assert.Contains("153*****57", text, StringComparison.Ordinal);
        Assert.Contains("9/9", text, StringComparison.Ordinal);
    }
}
