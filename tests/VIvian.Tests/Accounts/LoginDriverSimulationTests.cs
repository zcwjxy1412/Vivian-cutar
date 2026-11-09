using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using Xunit;
using static VIvian.Tests.Accounts.LoginFlowTests;

namespace VIvian.Tests.Accounts;

/// <summary>
/// Drives <see cref="StarRailLoginDriver"/> against a simulated game: screens are scripted OCR lines and clicks are
/// mapped back to the text under the cursor. This is a simulation; it does not prove real-game behaviour.
/// </summary>
public sealed class LoginDriverSimulationTests
{
    private static readonly SwitchPolicy Fast = new(LoginScreenTimeout: TimeSpan.FromSeconds(20),
        GameStartTimeout: TimeSpan.FromSeconds(20), InGameTimeout: TimeSpan.FromSeconds(5));

    private static LoginTarget B(string? previousUid = "100000001") =>
        new("139****11", "139*****11", 1, null, "153*****57", previousUid);

    [Fact]
    public async Task AutomaticAToBLogsOutSelectsVerifiesAndEntersWithoutTouchingEnterFirst()
    {
        var game = FakeGame.Standard();
        var driver = game.Driver();

        var outcome = await driver.SwitchAsync(B(), Fast, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal("100000002", outcome.Uid);
        Assert.Equal("verified", outcome.Evidence!.Verdict);
        Assert.Equal("139*****11", outcome.Evidence.ConfirmedSelectionMasked);
        Assert.Equal(new[] { "退出登录", "确定", "153*****57", "139*****11", "进入游戏", "点击进入" }, game.ClickedTexts);
        // "点击进入" is pressed only after the selected account was confirmed and "进入游戏" pressed.
        Assert.True(game.ClickedTexts.IndexOf("点击进入") > game.ClickedTexts.IndexOf("进入游戏"));
        Assert.Equal(0, game.UnmappedClicks);
    }

    [Fact]
    public async Task UnknownScreenProducesNoClicksAndStops()
    {
        var game = FakeGame.Standard();
        game.Screen = "garbage";
        var outcome = await game.Driver(new LoginFlowBudget(MaxUnknownStreak: 4)).SwitchAsync(B(), Fast, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.True(outcome.Fatal);
        Assert.Empty(game.ClickedTexts);
        Assert.Equal(0, game.UnmappedClicks);
    }

    [Fact]
    public async Task AuthenticationChallengeStopsForTheUserWithoutClicks()
    {
        var game = FakeGame.Standard();
        game.Screen = "captcha";
        var outcome = await game.Driver().SwitchAsync(B(), Fast, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.True(outcome.NeedsUser);
        Assert.True(outcome.Fatal);
        Assert.Empty(game.ClickedTexts);
    }

    [Fact]
    public async Task SelectionThatDoesNotStickNeverPressesEnterGame()
    {
        var game = FakeGame.Standard();
        game.Rules[("list", "139*****11")] = "cardA"; // the game keeps showing A after B is clicked
        var outcome = await game.Driver().SwitchAsync(B(), Fast, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.True(outcome.Fatal);
        Assert.DoesNotContain("进入游戏", game.ClickedTexts);
        Assert.Equal("mismatch", outcome.Evidence!.Verdict);
    }

    [Fact]
    public async Task TargetMissingFromSavedListIsSkippedButNotFatal()
    {
        var game = FakeGame.Standard();
        var outcome = await game.Driver().SwitchAsync(new LoginTarget("158****22", "158*****22", 2, null), Fast,
            CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.False(outcome.Fatal);
        Assert.DoesNotContain("进入游戏", game.ClickedTexts);
    }

    [Fact]
    public async Task SameUidAsPreviousAccountStopsTheBatch()
    {
        var game = FakeGame.Standard();
        var outcome = await game.Driver().SwitchAsync(B(previousUid: "100000002"), Fast, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.True(outcome.Fatal);
        Assert.Contains("上一个账号相同", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameRestartAfterSelectionBreaksTheIdentityChain()
    {
        var game = FakeGame.Standard();
        game.Rules[("cardB", "进入游戏")] = "titleB";
        game.OnEnter = () => game.ProcessGeneration++; // simulate the game being restarted underneath us
        var outcome = await game.Driver().SwitchAsync(B(), Fast, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.True(outcome.Fatal);
    }

    [Fact]
    public async Task OccludedFrameAsksTheElevatedHostToForegroundOnce()
    {
        var game = FakeGame.Standard();
        game.OccludedScans = 1;
        var outcome = await game.Driver().SwitchAsync(B(), Fast, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(1, game.ForegroundCalls);
    }

    [Fact]
    public async Task InWorldGameIsRestartedThenLoggedOut()
    {
        var game = FakeGame.Standard();
        game.Screen = "world";
        var outcome = await game.Driver().EnsureLoginAsync(Fast, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(1, game.Kills);
        Assert.Equal(1, game.Starts);
        Assert.Equal(new[] { "退出登录", "确定" }, game.ClickedTexts);
    }

    [Fact]
    public async Task StopGameRefusesWhenProcessHasNoProvableWindow()
    {
        var game = FakeGame.Standard();
        game.WindowVisible = false;
        game.ProcessRunning = true;
        var outcome = await game.Driver().StopGameAsync(CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal(0, game.Kills);
    }

    [Fact]
    public async Task CancellationStopsTheFlow()
    {
        var game = FakeGame.Standard();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => game.Driver().SwitchAsync(B(), Fast, cts.Token));
        Assert.Empty(game.ClickedTexts);
    }

    [Fact]
    public async Task ColdStartWaitsForWindowWithoutLaunchingTwice()
    {
        var game = FakeGame.Standard();
        game.Screen = "none";
        game.WindowVisible = false;
        game.ProcessRunning = false;
        game.OnStart = () =>
        {
            game.WindowPollsRemaining = 3;
            game.ProcessPollsRemaining = 2;
        };

        var outcome = await game.Driver().RestartToLoginAsync(Fast, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(1, game.Starts);
    }

    [Fact]
    public async Task StartupTimeoutNeverRetriesLaunchOrClicks()
    {
        var game = FakeGame.Standard();
        game.Screen = "none";
        game.ProcessRunning = false;
        game.OnStart = () => game.WindowPollsRemaining = int.MaxValue;

        var outcome = await game.Driver().RestartToLoginAsync(Fast, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Fatal);
        Assert.Contains("等待星铁窗口超时", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(1, game.Starts);
        Assert.Empty(game.ClickedTexts);
    }

    [Fact]
    public async Task WindowlessExistingClientIsReusedWhenItsWindowAppears()
    {
        var game = FakeGame.Standard();
        game.WindowPollsRemaining = 3;

        var outcome = await game.Driver().EnsureLoginAsync(Fast, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(0, game.Starts);
        Assert.Equal(0, game.Kills);
    }

    [Fact]
    public async Task RemainingClientBlocksRestartEvenAfterTargetExits()
    {
        var game = FakeGame.Standard();
        game.ClientRemainsAfterKill = true;

        var outcome = await game.Driver().RestartToLoginAsync(Fast, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Fatal);
        Assert.Equal(1, game.Kills);
        Assert.Equal(0, game.Starts);
    }

    // ---- simulated game ----------------------------------------------------------------------------------------

    internal sealed class FakeGame : ILoginScreenSource, IInputHost, IGameWindowLocator, IGameSessionVerifier
    {
        public Dictionary<string, RecognizedText[]> Screens { get; } = new()
        {
            ["titleA"] = TitleWithLogout, ["confirm"] = LogoutConfirm, ["cardA"] = CardA, ["list"] = ListAB,
            ["cardB"] = CardB, ["titleB"] = [T("点击进入", 960, 900)], ["world"] = World, ["garbage"] = Garbage,
            ["captcha"] = Captcha,
        };
        public Dictionary<(string Screen, string Text), string> Rules { get; } = new()
        {
            [("titleA", "退出登录")] = "confirm", [("confirm", "确定")] = "cardA", [("cardA", "153*****57")] = "list",
            [("list", "139*****11")] = "cardB", [("list", "153*****57")] = "cardA", [("cardB", "进入游戏")] = "titleB",
            [("titleB", "点击进入")] = "world",
        };
        public string Screen { get; set; } = "titleA";
        public List<string> ClickedTexts { get; } = [];
        public int UnmappedClicks { get; private set; }
        public int ForegroundCalls { get; private set; }
        public int Kills { get; private set; }
        public int Starts { get; private set; }
        public int OccludedScans { get; set; }
        public int ProcessGeneration { get; set; }
        public bool WindowVisible { get; set; } = true;
        public bool ProcessRunning { get; set; } = true;
        public Action? OnEnter { get; set; }
        public Action? OnStart { get; set; }
        public int WindowPollsRemaining { get; set; }
        public int ProcessPollsRemaining { get; set; }
        public bool ClientRemainsAfterKill { get; set; }

        public static FakeGame Standard() => new();

        public StarRailLoginDriver Driver(LoginFlowBudget? budget = null)
        {
            var now = DateTimeOffset.UnixEpoch;
            return new(this, this, this, () => @"C:\Game\StarRail.exe", null, this, (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                now += delay;
                return Task.CompletedTask;
            }, budget, () => now);
        }

        private StarRailWindowInfo Window => new(new IntPtr(42), 1000 + ProcessGeneration, "崩坏：星穹铁道", 1920, 1080, 0, 0,
            new DateTimeOffset(2026, 11, 7, 0, 0, ProcessGeneration, TimeSpan.Zero));

        public Task<LoginScreenView> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Find() is null)
                return Task.FromResult(new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.WindowNotFound, "无窗口"), null, null, null));
            if (OccludedScans > 0)
            {
                OccludedScans--;
                return Task.FromResult(new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.WindowOccluded, "被遮挡"),
                    null, Window, null));
            }
            var lines = Screens[Screen];
            var frame = new CapturedFrame(new byte[16], 2, 2, "test");
            var result = LoginScreenAnalyzer.Analyze(1920, 1080, lines);
            return Task.FromResult(new LoginScreenView(result, frame, Window, null) { Lines = lines });
        }

        public Task<LoginScreenView> CaptureAsync(CancellationToken cancellationToken) => ScanAsync(cancellationToken);

        public Task<InputResponse> ForegroundAsync(StarRailWindowInfo window, CancellationToken cancellationToken)
        {
            ForegroundCalls++;
            return Task.FromResult(InputResponse.Success("foreground"));
        }

        public Task<InputResponse> ClickAsync(StarRailWindowInfo window, int x, int y, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hit = Screens[Screen].FirstOrDefault(l => Math.Abs(l.CenterX - x) <= 2 && Math.Abs(l.CenterY - y) <= 2);
            if (hit is null) { UnmappedClicks++; return Task.FromResult(InputResponse.Success("nothing")); }
            var text = LoginStateClassifier.Compact(hit.Text);
            ClickedTexts.Add(text);
            if (Rules.TryGetValue((Screen, text), out var next))
            {
                if (text == "进入游戏") OnEnter?.Invoke();
                Screen = next;
            }
            return Task.FromResult(InputResponse.Success("click"));
        }

        public Task<InputResponse> KillAsync(StarRailWindowInfo window, CancellationToken cancellationToken)
        {
            Kills++;
            Screen = "none";
            ProcessRunning = ClientRemainsAfterKill;
            ProcessGeneration++;
            return Task.FromResult(InputResponse.Success("killed"));
        }

        public Task<InputResponse> StartGameAsync(string gamePath, CancellationToken cancellationToken)
        {
            Starts++;
            OnStart?.Invoke();
            Screen = "titleA";
            ProcessRunning = true;
            WindowVisible = true;
            return Task.FromResult(InputResponse.Success("started"));
        }

        public StarRailWindowInfo? Find()
        {
            if (WindowPollsRemaining > 0)
            {
                WindowPollsRemaining--;
                return null;
            }
            return WindowVisible && Screen != "none" ? Window : null;
        }
        public bool IsProcessRunning()
        {
            if (ProcessPollsRemaining > 0)
            {
                ProcessPollsRemaining--;
                return false;
            }
            return ProcessRunning;
        }
        public bool IsSameProcessAlive(StarRailWindowInfo window) =>
            Screen != "none" && window.ProcessStartedAt == Window.ProcessStartedAt;

        public Task<SessionVerification> VerifyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Screen == "world"
                ? new SessionVerification(true, "100000002", 0.95, "已进入游戏")
                : new SessionVerification(false, null, 0.2, "未进入"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>UAC session behaviour of the elevated host, with a fake channel (no real UAC prompt).</summary>
public sealed class ElevatedInputHostSessionTests
{
    private static readonly StarRailWindowInfo Window = new(new IntPtr(42), 1000, "星铁", 1920, 1080, 0, 0);

    [Fact]
    public async Task OneBatchLaunchesTheHostOnceAndReusesIt()
    {
        var launches = 0;
        await using var host = new ElevatedInputHost(_ => { launches++; return Task.FromResult<IInputChannel>(new FakeChannel()); });
        host.BeginBatch();
        for (var i = 0; i < 5; i++) Assert.True((await host.ClickAsync(Window, 1, 1, CancellationToken.None)).Ok);
        Assert.Equal(1, launches);
        Assert.Equal(1, host.LaunchCount);
        Assert.True(await host.EndBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RefusedPromptIsNotRepeatedWithinTheBatch()
    {
        var launches = 0;
        await using var host = new ElevatedInputHost(_ =>
        {
            launches++;
            throw new InputHostUnavailableException("已取消管理员授权", InputHostFailureKind.Refused);
        });
        host.BeginBatch();
        var first = await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.ClickAsync(Window, 1, 1, CancellationToken.None));
        var second = await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.ClickAsync(Window, 1, 1, CancellationToken.None));
        Assert.Equal(InputHostFailureKind.Refused, first.Kind);
        Assert.Equal(InputHostFailureKind.Refused, second.Kind);
        Assert.Equal(1, launches);
        Assert.Equal(InputHostState.Refused, host.State);

        host.BeginBatch(); // a new batch may ask again
        await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.ClickAsync(Window, 1, 1, CancellationToken.None));
        Assert.Equal(2, launches);
    }

    [Fact]
    public async Task HostCrashIsReportedAsLostAndNotRelaunched()
    {
        var launches = 0;
        var channel = new FakeChannel();
        await using var host = new ElevatedInputHost(_ => { launches++; return Task.FromResult<IInputChannel>(channel); });
        host.BeginBatch();
        Assert.True((await host.ClickAsync(Window, 1, 1, CancellationToken.None)).Ok);
        channel.Broken = true;
        var lost = await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.ClickAsync(Window, 1, 1, CancellationToken.None));
        Assert.Equal(InputHostFailureKind.Lost, lost.Kind);
        var again = await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.ClickAsync(Window, 1, 1, CancellationToken.None));
        Assert.Equal(InputHostFailureKind.Lost, again.Kind);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task DeadProcessIsDetectedBeforeTheNextCommand()
    {
        var channel = new FakeChannel();
        var launches = 0;
        await using var host = new ElevatedInputHost(_ => { launches++; return Task.FromResult<IInputChannel>(channel); });
        host.BeginBatch();
        await host.ClickAsync(Window, 1, 1, CancellationToken.None);
        channel.Alive = false;
        var ex = await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.ClickAsync(Window, 1, 1, CancellationToken.None));
        Assert.Equal(InputHostFailureKind.Lost, ex.Kind);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task EndBatchReportsUnconfirmedHostExit()
    {
        await using var host = new ElevatedInputHost(_ => Task.FromResult<IInputChannel>(new FakeChannel { ExitConfirmed = false }));
        host.BeginBatch();
        await host.ClickAsync(Window, 1, 1, CancellationToken.None);
        Assert.False(await host.EndBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task KillCarriesProcessStartTimeForIdentity()
    {
        var channel = new FakeChannel();
        await using var host = new ElevatedInputHost(_ => Task.FromResult<IInputChannel>(channel));
        var started = new DateTimeOffset(2026, 11, 7, 1, 2, 3, TimeSpan.Zero);
        await host.KillAsync(Window with { ProcessStartedAt = started }, CancellationToken.None);
        var request = Assert.Single(channel.Requests);
        Assert.Equal("kill", request.Op);
        Assert.Equal(1000, request.ProcessId);
        Assert.Equal(started.UtcTicks, request.ProcessStartUtcTicks);
    }

    [Fact]
    public async Task TimeoutDuringLaunchIsTyped()
    {
        await using var host = new ElevatedInputHost(_ =>
            throw new InputHostUnavailableException("等待超时", InputHostFailureKind.Timeout));
        host.BeginBatch();
        var ex = await Assert.ThrowsAsync<InputHostUnavailableException>(() => host.StartGameAsync("x", CancellationToken.None));
        Assert.Equal(InputHostFailureKind.Timeout, ex.Kind);
        Assert.Equal(InputHostState.Failed, host.State);
    }

    private sealed class FakeChannel : IInputChannel
    {
        public bool Alive { get; set; } = true;
        public bool Broken { get; set; }
        public bool ExitConfirmed { get; set; } = true;
        public List<InputRequest> Requests { get; } = [];
        public bool IsAlive => Alive;

        public Task<InputResponse> SendAsync(InputRequest request, CancellationToken cancellationToken)
        {
            if (Broken) throw new IOException("pipe broken");
            Requests.Add(request);
            return Task.FromResult(InputResponse.Success());
        }

        public Task<bool> ShutdownAsync(CancellationToken cancellationToken) => Task.FromResult(ExitConfirmed);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
