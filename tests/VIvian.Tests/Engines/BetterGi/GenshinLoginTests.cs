using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinLoginTests
{
    private const string Target = "188****63";

    [Theory]
    [InlineData("188******63", 1)]
    [InlineData("１８８ ＊＊＊＊ ６３", 0.5)]
    [InlineData("188••••63", 2)]
    public void MatchingMaskOnAlignedCardProposesTheCurrentEnterLabel(string mask, double scale)
    {
        var scaled = Card(mask).Select(line => line with
        {
            X = line.X * scale, Y = line.Y * scale, Width = line.Width * scale, Height = line.Height * scale,
        }).ToArray();
        var screen = GenshinLoginScreen.Analyze((int)(1920 * scale), (int)(1080 * scale), scaled);
        var step = new GenshinLoginSession(Target).Next(screen);

        Assert.Equal(GenshinLoginScreenState.SavedAccountCard, screen.State);
        Assert.Equal(Target, screen.Account?.NormalizedPhone);
        Assert.Equal(0.82, screen.Account?.Confidence);
        Assert.Equal("", screen.Account?.NearbyText);
        Assert.Equal(GenshinLoginStepKind.EnterSavedAccount, step.Kind);
        Assert.Equal(new ScreenRegion(900 * scale, 592 * scale, 120 * scale, 28 * scale), step.ActionRegion);
    }

    [Theory]
    [InlineData("188****64")]
    [InlineData("188****1263")]
    [InlineData("133****63")]
    public void ARecognizedCardForAnotherAccountDoesNotProposeInput(string mask)
    {
        var step = new GenshinLoginSession(Target).Next(Read(Card(mask)));

        Assert.Equal(GenshinLoginStepKind.TargetNotVisible, step.Kind);
        Assert.Null(step.ActionRegion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("18812345663")]
    [InlineData("account: 188****63")]
    public void TheRequestedAccountMustBeAnExplicitMaskedIdentity(string? target)
    {
        var step = new GenshinLoginSession(target).Next(Read(Card()));

        Assert.Equal(GenshinLoginStepKind.InvalidTarget, step.Kind);
        Assert.Null(step.ActionRegion);
    }

    [Theory]
    [InlineData("188******63")]
    [InlineData("133******71")]
    [InlineData("133")]
    [InlineData("18812345663")]
    public void ASecondPossibleAccountRowMakesThePanelAmbiguous(string extra)
    {
        var screen = Read([.. Card(), new(extra, 745, 510, 140, 22)]);
        var step = new GenshinLoginSession(Target).Next(screen);

        Assert.Equal(GenshinLoginStepKind.AmbiguousAccount, step.Kind);
        Assert.Null(screen.Account);
        Assert.Null(step.ActionRegion);
    }

    [Theory]
    [InlineData("18863")]
    [InlineData("18812345663")]
    [InlineData("手机号 188****63")]
    public void IncompleteFullOrEmbeddedMasksDoNotIdentifyASelectedAccount(string text)
    {
        var screen = Read(Card(text));

        Assert.Equal(GenshinLoginScreenState.Unrecognized, screen.State);
        Assert.Null(screen.Account);
    }

    [Fact]
    public void SeparateDigitRunsDoNotReconstructAnIdentity()
    {
        var screen = Read([.. Card("188"), new("63", 860, 440, 28, 22)]);

        Assert.Null(screen.Account);
        Assert.Null(new GenshinLoginSession(Target).Next(screen).ActionRegion);
    }

    [Theory]
    [InlineData("miHoYo")]
    [InlineData("进入游戏")]
    [InlineData("登录其他账号")]
    public void EachReferenceAnchorIsRequired(string absent)
    {
        var screen = Read(Card().Where(line => line.Text != absent).ToArray());

        Assert.Equal(GenshinLoginScreenState.Unrecognized, screen.State);
        Assert.Null(new GenshinLoginSession(Target).Next(screen).ActionRegion);
    }

    [Fact]
    public void RepeatedEnterLabelsDoNotSelectOneByPosition()
    {
        var screen = Read([.. Card(), new("进入游戏", 60, 800, 120, 28)]);

        Assert.Equal(GenshinLoginScreenState.Unrecognized, screen.State);
        Assert.Null(new GenshinLoginSession(Target).Next(screen).ActionRegion);
    }

    [Fact]
    public void BackgroundLabelsAndMasksMustAlignAsTheSamePanel()
    {
        var unaligned = Card().Select(line => line.Text == "登录其他账号" ? line with { X = 1400 } : line);
        var screen = Read(unaligned.ToArray());

        Assert.Equal(GenshinLoginScreenState.Unrecognized, screen.State);
        Assert.Null(new GenshinLoginSession(Target).Next(screen).ActionRegion);
    }

    [Fact]
    public void OffscreenBoundsCannotBecomeAnActionTarget()
    {
        var offscreen = Card().Select(line => line.Text == "进入游戏" ? line with { X = -10 } : line);
        var screen = Read(offscreen.ToArray());

        Assert.Null(new GenshinLoginSession(Target).Next(screen).ActionRegion);
    }

    [Theory]
    [InlineData("请输入密码")]
    [InlineData("请输入验证码")]
    [InlineData("拖动滑块完成验证")]
    [InlineData("安全验证")]
    [InlineData("实名 认证")]
    [InlineData("请阅读并同意用户协议")]
    public void AUserOnlyPromptTakesPriorityOverARecognizedCard(string prompt)
    {
        var screen = Read([.. Card(), new(prompt, 850, 550, 220, 26)]);
        var session = new GenshinLoginSession(Target);

        Assert.Equal(GenshinLoginScreenState.NeedsUser, screen.State);
        Assert.Equal(GenshinLoginStepKind.NeedsUser, session.Next(screen).Kind);
        Assert.Equal(GenshinLoginStepKind.NeedsUser, session.Next(Read(Card())).Kind);
        Assert.Null(session.Next(Read(Card())).ActionRegion);
    }

    [Theory]
    [InlineData("点击进入")]
    [InlineData("退出登录")]
    [InlineData("UID: 800000000")]
    public void UnsupportedScreensDoNotProposeAWorldOrLogoutAction(string text)
    {
        var screen = Read([new(text, 880, 600, 160, 25)]);
        var step = new GenshinLoginSession(Target).Next(screen);

        Assert.Equal(GenshinLoginStepKind.UnrecognizedScreen, step.Kind);
        Assert.Null(step.ActionRegion);
    }

    [Theory]
    [InlineData("退出登录")]
    [InlineData("登录失败，请重试")]
    [InlineData("确定")]
    public void AnUnsupportedModalBlocksTheSavedCardBehindIt(string text)
    {
        var step = new GenshinLoginSession(Target).Next(Read([.. Card(), new(text, 880, 530, 160, 25)]));

        Assert.Equal(GenshinLoginStepKind.UnrecognizedScreen, step.Kind);
        Assert.Null(step.ActionRegion);
    }

    [Fact]
    public void APreviousStopDoesNotResumeWhenAnotherFrameLooksEligible()
    {
        var session = new GenshinLoginSession(Target);
        Assert.Equal(GenshinLoginStepKind.TargetNotVisible, session.Next(Read(Card("133****71"))).Kind);

        Assert.Null(session.Next(Read(Card())).ActionRegion);
    }

    [Fact]
    public void TheSavedAccountButtonIsProposedOnceAndAUidDoesNotProveWorldEntry()
    {
        var session = new GenshinLoginSession(Target);
        Assert.Equal(GenshinLoginStepKind.EnterSavedAccount, session.Next(Read(Card())).Kind);

        var step = session.Next(Read([new("UID: 800000000", 1710, 1000, 160, 25)]));

        Assert.Equal(GenshinLoginStepKind.WorldUnverified, step.Kind);
        Assert.Null(step.ActionRegion);
        Assert.Null(session.Next(Read(Card())).ActionRegion);
    }

    [Fact]
    public void ARepeatedCardAfterTheEnterProposalIsNotRetried()
    {
        var session = new GenshinLoginSession(Target);
        var screen = Read(Card());
        Assert.Equal(GenshinLoginStepKind.EnterSavedAccount, session.Next(screen).Kind);

        var step = session.Next(screen);

        Assert.Equal(GenshinLoginStepKind.WorldUnverified, step.Kind);
        Assert.Null(step.ActionRegion);
    }

    [Fact]
    public void AnAuthenticationPromptAfterTheEnterProposalStillStopsForTheUser()
    {
        var session = new GenshinLoginSession(Target);
        Assert.Equal(GenshinLoginStepKind.EnterSavedAccount, session.Next(Read(Card())).Kind);

        var step = session.Next(Read([new("请完成安全验证", 880, 600, 160, 25)]));

        Assert.Equal(GenshinLoginStepKind.NeedsUser, step.Kind);
        Assert.Null(step.ActionRegion);
    }

    [Fact]
    public void PartialLogoOnTheFourKCardRequiresAnAlignedCaret()
    {
        var lines = DoubleSize([
            new("HO", 935, 377, 59, 43),
            new(Target, 788, 487, 117, 18),
            new("进入游戏", 920, 610, 80, 19),
            new("登录其他账号", 900, 677, 120, 18),
        ]);
        var caret = new ScreenRegion(2382, 980, 36, 24);

        Assert.Equal(GenshinLoginScreenState.Unrecognized, GenshinLoginScreen.Analyze(3840, 2160, lines).State);
        Assert.Equal(GenshinLoginScreenState.Unrecognized,
            GenshinLoginScreen.Analyze(3840, 2160, lines, caret with { Y = 1250 }).State);
        var card = GenshinLoginScreen.Analyze(3840, 2160, lines, caret);
        Assert.Equal(Target, card.Account?.NormalizedPhone);
        Assert.Equal(new ScreenRegion(1840, 1220, 160, 38), card.EnterLabel);
    }

    [Fact]
    public void FourKExpandedListRemovesOnlyTheRepeatedHeaderAndKeepsTheActualRows()
    {
        var rows = GenshinLoginScreen.AnalyzeExpandedList(3840, 2160, DoubleSize(Expanded()),
            new(2382, 980, 36, 24));

        Assert.Equal([Target, "139****74"], rows.Select(row => row.NormalizedPhone));
        Assert.Equal([1106, 1284], rows.Select(row => row.Y));
        Assert.Empty(GenshinLoginScreen.AnalyzeExpandedList(3840, 2160, DoubleSize(Expanded()),
            new(2382, 1260, 36, 24)));
    }

    [Theory]
    [InlineData("188******63")]
    [InlineData("13912345674")]
    [InlineData("13")]
    public void AnAmbiguousOrIncompleteExpandedListCannotYieldAnActionableRow(string secondRow)
    {
        Assert.Empty(GenshinLoginScreen.AnalyzeExpandedList(3840, 2160, DoubleSize(Expanded(secondRow)),
            new(2382, 980, 36, 24)));
    }

    [Theory]
    [InlineData("输入密码")]
    [InlineData("确定")]
    [InlineData("进入游戏")]
    public void AnOverlayOrTheCollapsedEnterLabelBlocksExpandedListRecognition(string text)
    {
        var lines = DoubleSize([.. Expanded(), new(text, 880, 600, 100, 20)]);

        Assert.Empty(GenshinLoginScreen.AnalyzeExpandedList(3840, 2160, lines, new(2382, 980, 36, 24)));
    }

    [Fact]
    public void FourKOpenCaretMatchesOnlyTheFlippedTemplate()
    {
        const int width = 18;
        const int height = 12;
        var icon = new CapturedFrame(new byte[width * height * 4], width, height, "synthetic-caret");
        var openIcon = icon with { Bgra = new byte[icon.Bgra.Length] };
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var strokeY = 9 - Math.Abs(x - 8);
            var shade = (byte)(x is >= 2 and <= 15 && Math.Abs(strokeY - y) <= 1 ? 170 : 255);
            var pixel = new byte[] { shade, shade, shade, 255 };
            pixel.CopyTo(icon.Bgra, (y * width + x) * 4);
            pixel.CopyTo(openIcon.Bgra, ((height - y - 1) * width + x) * 4);
        }
        var scaled = openIcon.Scale(2);
        var frame = new CapturedFrame(new byte[96 * 2160 * 4], 96, 2160, "synthetic-frame");
        for (var y = 0; y < scaled.Height; y++)
            Array.Copy(scaled.Bgra, y * scaled.Width * 4, frame.Bgra,
                ((920 + y) * frame.Width + 21) * 4, scaled.Width * 4);
        var search = new ScreenRegion(19, 918, 40, 28);

        Assert.Null(GenshinLoginTemplates.Match(frame, icon, search, default));
        Assert.Equal(new ScreenRegion(21, 920, 36, 24),
            GenshinLoginTemplates.Match(frame, icon, search, default, flipVertically: true));
    }

    private static RecognizedText[] DoubleSize(RecognizedText[] lines) => lines.Select(line => line with
    {
        X = line.X * 2, Y = line.Y * 2, Width = line.Width * 2, Height = line.Height * 2,
    }).ToArray();

    private static RecognizedText[] Expanded(string secondRow = "139******74") =>
    [
        new("HO", 935, 377, 59, 43),
        new(Target, 788, 487, 117, 18),
        new(Target, 788, 553, 117, 17),
        new("刚 刚", 820, 594, 32, 15),
        new(secondRow, 788, 642, 117, 17),
        new("1 天 前", 818, 682, 42, 16),
        new("登 录 其 他 账 号", 900, 740, 120, 18),
    ];

    private static GenshinLoginScreen Read(IReadOnlyList<RecognizedText> lines) =>
        GenshinLoginScreen.Analyze(1920, 1080, lines);

    private static RecognizedText[] Card(string mask = Target) =>
    [
        new("miHoYo", 855, 290, 210, 65),
        new(mask, 745, 440, 140, 22),
        new("进入游戏", 900, 592, 120, 28),
        new("登录其他账号", 875, 683, 170, 24),
    ];
}
