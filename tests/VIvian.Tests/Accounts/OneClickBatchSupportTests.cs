using System.Text;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using Xunit;
using static VIvian.Tests.Accounts.LoginFlowTests;

namespace VIvian.Tests.Accounts;

/// <summary>Support for the one-click multi-account batch. Screens are simulated OCR output.</summary>
public sealed class OneClickBatchSupportTests
{
    [Fact]
    public void LogoutDialogOverTheTitleScreenIsTheConfirmationNotTheTitle()
    {
        var lines = new[] { T("点击进入", 960, 900), T("是否退出登录", 960, 480, 260), T("取消", 820, 640, 80), T("确定", 1100, 640, 80) };
        var o = LoginStateClassifier.Classify(lines);
        Assert.Equal(LoginScreenState.LogoutConfirm, o.State);
        Assert.Equal("确定", o.ActionTarget!.Text);
    }

    [Fact]
    public void ExitGameIsNeverTreatedAsLogout()
    {
        var o = LoginStateClassifier.Classify([T("点击进入", 960, 900), T("退出游戏", 100, 1000)]);
        Assert.Equal(LoginScreenState.Title, o.State);
        Assert.Null(o.ActionTarget);
    }

    [Theory]
    [InlineData("登出")]
    [InlineData("切换账号")]
    [InlineData("退出账号")]
    public void TitleLogoutCaptionsAreRecognised(string caption)
    {
        var o = LoginStateClassifier.Classify([T("点击进入", 960, 900), T(caption, 1850, 60, 60)]);
        Assert.Equal(LoginScreenState.Title, o.State);
        Assert.Equal(caption, o.ActionTarget!.Text);
    }

    [Fact]
    public void ResolutionValueRoundTripsInUpstreamFormat()
    {
        // The exact bytes March7thAssistant writes: json.dumps(...) + NUL, REG_BINARY.
        var upstream = Encoding.UTF8.GetBytes("{\"width\": 1920, \"height\": 1080, \"isFullScreen\": false}\0");
        Assert.Equal(new StarRailResolution.Resolution(1920, 1080, false), StarRailResolution.Parse(upstream));
        Assert.Equal(upstream, StarRailResolution.Serialize(new(1920, 1080, false)));
        Assert.Equal(new StarRailResolution.Resolution(2560, 1440, true),
            StarRailResolution.Parse(Encoding.UTF8.GetBytes("{\"Width\":2560,\"Height\":1440,\"IsFullScreen\":true}\0")));
        Assert.Null(StarRailResolution.Parse(null));
    }

    [Theory]
    [InlineData(2560, 1440, false)]
    [InlineData(1920, 1080, true)]
    public void TargetFollowsUpstreamFullScreenRule(int w, int h, bool full) =>
        Assert.Equal(new StarRailResolution.Resolution(1920, 1080, full), StarRailResolution.Target(w, h));

    [Fact]
    public async Task LaunchAppliesSettingsBeforeStartAndRestoresAfterTheWindowAppears()
    {
        var game = LoginDriverSimulationTests.FakeGame.Standard();
        game.Screen = "none";
        game.ProcessRunning = false;
        var events = new List<string>();
        game.OnStart = () => events.Add("start");
        var driver = new StarRailLoginDriver(game, game, game, () => @"C:\Game\StarRail.exe", null, game,
            (_, _) => Task.CompletedTask, null, null,
            () => { events.Add("apply"); return new Disposable(() => events.Add("restore")); });

        var outcome = await driver.EnsureLoginAsync(new SwitchPolicy(GameStartTimeout: TimeSpan.FromSeconds(20)), CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(new[] { "apply", "start", "restore" }, events);
    }

    [Fact]
    public async Task FailedFlowSavesLocalEvidenceOfTheStoppingScreen()
    {
        var game = LoginDriverSimulationTests.FakeGame.Standard();
        game.Screens["titleA"] = TitleWithoutLogout;
        var saved = new List<string>();
        var driver = new StarRailLoginDriver(game, game, game, () => "x", null, game, (_, _) => Task.CompletedTask,
            null, null, null, (view, name) => { saved.Add(name); return Task.CompletedTask; });
        var outcome = await driver.EnsureLoginAsync(new SwitchPolicy(GameStartTimeout: TimeSpan.FromSeconds(20)), CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Single(saved);
        Assert.Contains("Title", saved[0], StringComparison.Ordinal);
        Assert.DoesNotContain("点击进入", game.ClickedTexts);
    }

    private sealed class Disposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>Elevated-VIvian claim of March7thAssistant's in-process run record.</summary>
public sealed class InProcessRunClaimTests
{
    private static readonly DateTimeOffset Root = DateTimeOffset.FromUnixTimeMilliseconds(1794120000123);

    private static string Record(int pid, double created, string task = "daily", string id = "20261108-101500-ab12") =>
        "[INFO] 开始\n" +
        $"{{\"run_id\": \"{id}\", \"status\": \"success\", \"task\": \"{task}\", \"pid\": {pid}, \"pid_create_time\": {created.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"lock\": \"acquired\", \"ended_at\": \"2026-11-08T10:30:00-05:00\"}}";

    [Fact]
    public void RecordOfOurOwnRootProcessIsClaimed() =>
        Assert.Equal("20261108-101500-ab12",
            VIvian.Infrastructure.Engines.Cli.March7thTaskContract.CaptureInProcessRunId(Record(4242, 1794120000.2), "daily", 4242, Root));

    [Theory]
    [InlineData(4243, 1794120000.2, "daily")]   // another process (e.g. an elevated worker)
    [InlineData(4242, 1794119990.0, "daily")]   // same PID, different process (reused PID)
    [InlineData(4242, 1794120000.2, "routine")] // another task
    public void AnythingElseIsNotClaimed(int pid, double created, string task) =>
        Assert.Null(VIvian.Infrastructure.Engines.Cli.March7thTaskContract.CaptureInProcessRunId(Record(pid, created, task), "daily", 4242, Root));

    [Fact]
    public void MalformedOrMissingRecordsAreNotClaimed()
    {
        Assert.Null(VIvian.Infrastructure.Engines.Cli.March7thTaskContract.CaptureInProcessRunId("no json", "daily", 4242, Root));
        Assert.Null(VIvian.Infrastructure.Engines.Cli.March7thTaskContract.CaptureInProcessRunId(Record(4242, 1794120000.2, id: "../x"), "daily", 4242, Root));
        Assert.Null(VIvian.Infrastructure.Engines.Cli.March7thTaskContract.CaptureInProcessRunId("{broken", "daily", 4242, Root));
    }

    [Fact]
    public async Task HostLaunchFromAnElevatedVivianIsNotCountedAsAPrompt()
    {
        await using var host = new ElevatedInputHost(_ => Task.FromResult<IInputChannel>(new NullChannel()), launchPromptsUser: false);
        host.BeginBatch();
        await host.ClickAsync(new StarRailWindowInfo(new IntPtr(1), 1, "", 10, 10, 0, 0), 1, 1, CancellationToken.None);
        Assert.Equal(0, host.LaunchCount);
        Assert.Equal(InputHostState.Connected, host.State);
    }

    private sealed class NullChannel : IInputChannel
    {
        public bool IsAlive => true;
        public Task<InputResponse> SendAsync(InputRequest request, CancellationToken cancellationToken) => Task.FromResult(InputResponse.Success());
        public Task<bool> ShutdownAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
