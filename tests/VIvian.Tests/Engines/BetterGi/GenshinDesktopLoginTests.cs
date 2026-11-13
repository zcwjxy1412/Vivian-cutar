using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinDesktopLoginTests
{
    [Fact]
    public async Task SavedUidIsOptionalWhenVerifiedCardAndLoginContinuityArePresent()
    {
        var world = Frame(GenshinView.World);
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone), Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Gate), world, world, world);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);
        var result = await driver.SwitchAsync(B, Policy, default);
        Assert.True(result.Ok, result.Detail);
        Assert.Null((await driver.ConfirmWorldAsync(Guid.NewGuid(), B.NormalizedPhone, B.ObservedUid, default)).Uid);
    }

    [Fact]
    public async Task OwnedBatchRestartsClientInsteadOfOpeningTheInGameMenu()
    {
        var nextWindow = Window with { ProcessId = 999, Handle = (IntPtr)999, ProcessStartedAt = Window.ProcessStartedAt!.Value.AddMinutes(1) };
        GenshinDesktopFrame Next(GenshinView view, string? mask = null, string? second = null, string? uid = null) =>
            Frame(view, mask, second, uid) with { Window = nextWindow };
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Gate), Frame(GenshinView.World), Frame(GenshinView.World),
            Next(GenshinView.Card, A.NormalizedPhone),
            Next(GenshinView.Card, A.NormalizedPhone), Next(GenshinView.Dropdown, A.NormalizedPhone, B.NormalizedPhone),
            Next(GenshinView.Card, B.NormalizedPhone), Next(GenshinView.Gate),
            Next(GenshinView.World, uid: B.ObservedUid), Next(GenshinView.World, uid: B.ObservedUid),
            Next(GenshinView.World, uid: B.ObservedUid));
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero, stopOwnedClient: true);
        Assert.True((await driver.SwitchAsync(A, Policy, default)).Ok);
        var returned = await driver.RestartToLoginAsync(Policy, default);
        Assert.True(returned.Ok, returned.Detail);
        Assert.Equal(1, desktop.RestartCalls);
        Assert.DoesNotContain(GenshinInput.OpenMenu, desktop.Actions);
        Assert.True((await driver.SwitchAsync(B, Policy, default)).Ok);
        var world = await driver.ConfirmWorldAsync(Guid.NewGuid(), B.NormalizedPhone, B.ObservedUid, default);
        Assert.Equal(nextWindow.ProcessId, world.ProcessId);
        Assert.Equal(B.ObservedUid, world.Uid);
    }

    [Fact]
    public async Task DailyStopsAtTheFullGateAndRechecksItWithoutOpeningTheDoor()
    {
        var desktop = new Desktop(
            Frame(GenshinView.Card, A.NormalizedPhone), Frame(GenshinView.Dropdown, A.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone), Frame(GenshinView.CheckingUpdates),
            Frame(GenshinView.Gate), Frame(GenshinView.Gate));
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero, stopOwnedClient: true, handoffAtGate: true);

        var first = await driver.SwitchAsync(A, Policy, default);
        Assert.True(first.Ok, first.Detail);
        Assert.Equal(LoginSwitchStage.ReadyAtGate, first.Stage);
        Assert.Null(first.Evidence!.InGameAt);
        Assert.Equal([GenshinInput.OpenDropdown, GenshinInput.ChooseAccount, GenshinInput.EnterAccount], desktop.Actions);
        await driver.ConfirmGateAsync(A.NormalizedPhone, default);
        driver.RestoreGameForeground();
        Assert.Equal(Window, desktop.RestoredWindow);
        Assert.DoesNotContain(GenshinInput.OpenGate, desktop.Actions);
        Assert.Equal(0, desktop.RestartCalls);
        Assert.Equal(0, desktop.StopCalls);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("different-process")]
    [InlineData("different-account")]
    public async Task DailyRejectsAndRevokesStaleGateProof(string change)
    {
        var gate = Frame(GenshinView.Gate);
        var changed = change switch
        {
            "world" => gate with { View = GenshinView.World },
            "different-process" => gate with { Window = Window with { ProcessId = Window.ProcessId + 1 } },
            _ => gate,
        };
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone), Frame(GenshinView.Card, A.NormalizedPhone), gate, changed);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero, handoffAtGate: true);
        Assert.True((await driver.SwitchAsync(A, Policy, default)).Ok);

        var phone = change == "different-account" ? B.NormalizedPhone : A.NormalizedPhone;
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConfirmGateAsync(phone, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConfirmGateAsync(A.NormalizedPhone, default));
        Assert.DoesNotContain(GenshinInput.OpenGate, desktop.Actions);
    }

    [Fact]
    public async Task AuthenticodeReceivesASynchronousReadOnlyFileHandle()
    {
        await using var fixture = new BetterGiFixture();
        using var file = GenshinGameExecutable.OpenForSignatureVerification(Path.Combine(fixture.Root, "BetterGI.exe"));
        Assert.False(file.IsAsync);
        Assert.True(file.CanRead);
        Assert.False(file.CanWrite);
    }

    private static readonly StarRailWindowInfo Window = new((IntPtr)123, 45, "", 1920, 1080, 0, 0, DateTimeOffset.UtcNow);
    private static readonly LoginTarget A = new("130****01", "130******01", 0, null);
    private static readonly LoginTarget B = new("130****02", "130******02", 1, "234567891", A.MaskedPhone, "234567890");
    private static readonly SwitchPolicy Policy = new(StepTimeout: TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData(false, true, false, "Missing")]
    [InlineData(true, true, false, "Minimized")]
    [InlineData(true, false, false, "IncompatibleViewport")]
    [InlineData(true, false, true, "Background")]
    public void ColdLaunchCanRestoreOnlyItsVerifiedWindowBeforeCheckingTheViewport(
        bool owned, bool minimized, bool usableViewport, string expected)
    {
        Assert.Equal(expected, GenshinWindowsLoginDesktop.ClassifyLaunchWindow(owned, minimized, usableViewport).ToString());
    }

    [Theory]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void GateLogoutSearchIncludesTheWholeClientIcon(int height)
    {
        var scale = height / 1080d;
        var search = GenshinWindowsLoginDesktop.GateLogoutSearch(height);

        // Bounds of the pinned 36×39 visual template centered on the current client's logout icon.
        Assert.True(search.Contains(1806 * scale, 873 * scale));
        Assert.True(search.Contains(1842 * scale, 912 * scale));
    }

    [Theory]
    [InlineData("检查更新中…", true)]
    [InlineData("检查更新中...", true)]
    [InlineData("检查更新中", true)]
    [InlineData("检查更新失败", false)]
    [InlineData("点击进入", false)]
    public void OnlyTheGameUpdateCheckIsRecognizedAsWaiting(string text, bool expected) =>
        Assert.Equal(expected, GenshinWindowsLoginDesktop.IsUpdateCheckText(text));

    [Fact]
    public async Task CheckingForGameUpdatesDoesNotSendInput()
    {
        var desktop = new Desktop(Frame(GenshinView.CheckingUpdates));
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.FromSeconds(30));

        var result = await driver.EnsureLoginAsync(new SwitchPolicy(LoginScreenTimeout: TimeSpan.FromMilliseconds(500)), default);

        Assert.False(result.Ok);
        Assert.Contains("原神仍在检查更新", result.Detail);
        Assert.Empty(desktop.Actions);
    }

    [Theory]
    [InlineData(LoginScanFailure.WindowOccluded, null, "画面被遮挡")]
    [InlineData(LoginScanFailure.CaptureFailed, null, "未获得可核对的游戏画面")]
    [InlineData(null, null, "画面尚未识别")]
    [InlineData(null, "已识别大门文字，退出账号图标尚未核对。", "退出账号图标尚未核对")]
    public async Task LoginTimeoutDistinguishesRejectedCaptureFromAnUnrecognizedFrame(LoginScanFailure? captureFailure,
        string? visualDiagnostic, string expectedDetail)
    {
        var frame = Frame(GenshinView.Unknown) with { CaptureFailure = captureFailure, VisualDiagnostic = visualDiagnostic };
        var desktop = new Desktop(frame);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.FromSeconds(30));

        var result = await driver.EnsureLoginAsync(new SwitchPolicy(LoginScreenTimeout: TimeSpan.FromMilliseconds(500)), default);

        Assert.False(result.Ok);
        Assert.Contains(expectedDetail, result.Detail);
        Assert.Empty(desktop.Actions);
    }

    [Fact]
    public async Task AToBRequiresLogoutThenFreshTargetCardAndContinuousWorld()
    {
        var desktop = new Desktop(
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone, B.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Gate),
            Frame(GenshinView.World), Frame(GenshinView.World),
            Frame(GenshinView.World),
            Frame(GenshinView.World), Frame(GenshinView.Menu), Frame(GenshinView.LeaveWorld),
            Frame(GenshinView.Gate), Frame(GenshinView.ConfirmLogout), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone, B.NormalizedPhone),
            Frame(GenshinView.Card, B.NormalizedPhone), Frame(GenshinView.Gate),
            Frame(GenshinView.World, uid: B.ObservedUid), Frame(GenshinView.World, uid: B.ObservedUid),
            Frame(GenshinView.World, uid: B.ObservedUid));
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);

        Assert.True((await driver.EnsureLoginAsync(Policy, default)).Ok);
        Assert.Equal("verified", (await driver.SwitchAsync(A, Policy, default)).Evidence?.Verdict);
        var firstId = Guid.NewGuid();
        var firstWorld = await driver.ConfirmWorldAsync(firstId, A.NormalizedPhone, A.ObservedUid, default);
        Assert.Equal(firstId, firstWorld.AccountId);
        Assert.Null(firstWorld.Uid);
        Assert.True((await driver.RestartToLoginAsync(Policy, default)).Ok);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.ConfirmWorldAsync(firstId, A.NormalizedPhone, A.ObservedUid, default));
        Assert.Equal(1, desktop.PrepareCalls);
        var second = await driver.SwitchAsync(B, Policy, default);
        Assert.True(second.Ok, second.Detail);
        Assert.Equal(B.ObservedUid, second.Uid);
        var secondId = Guid.NewGuid();
        var secondWorld = await driver.ConfirmWorldAsync(secondId, B.NormalizedPhone, B.ObservedUid, default);
        Assert.Equal(secondId, secondWorld.AccountId);
        Assert.Equal(B.ObservedUid, secondWorld.Uid);
        Assert.Equal(Window.Handle, secondWorld.WindowHandle);
        Assert.Equal(Window.ProcessId, secondWorld.ProcessId);
        Assert.Equal(Window.ProcessStartedAt, secondWorld.ProcessStartedAt);
        Assert.Equal(firstWorld.WindowHandle, secondWorld.WindowHandle);
        Assert.Equal([GenshinInput.OpenDropdown, GenshinInput.ChooseAccount,
            GenshinInput.EnterAccount, GenshinInput.OpenGate, GenshinInput.OpenMenu,
            GenshinInput.LeaveMenu, GenshinInput.ConfirmLeave, GenshinInput.LogoutGate, GenshinInput.ConfirmLogout,
            GenshinInput.OpenDropdown, GenshinInput.ChooseAccount, GenshinInput.EnterAccount, GenshinInput.OpenGate],
            desktop.Actions);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("pid")]
    [InlineData("started")]
    [InlineData("foreground")]
    [InlineData("gate")]
    [InlineData("uid")]
    public async Task DailyHandoffStopsImmediatelyAndRevokesProofWhenTheWorldChanges(string change)
    {
        var frame = Frame(GenshinView.World, uid: B.ObservedUid);
        var changed = change switch
        {
            "handle" => frame with { Window = Window with { Handle = (IntPtr)999 } },
            "pid" => frame with { Window = Window with { ProcessId = Window.ProcessId + 1 } },
            "started" => frame with { Window = Window with { ProcessStartedAt = Window.ProcessStartedAt!.Value.AddSeconds(1) } },
            "foreground" => frame with { Window = null },
            "gate" => frame with { View = GenshinView.Gate },
            "uid" => frame with { Uid = "234567899" },
            _ => throw new InvalidOperationException(),
        };
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone), Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Gate), frame, frame, changed, frame);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);
        var id = Guid.NewGuid();
        Assert.True((await driver.SwitchAsync(B, Policy, default)).Ok);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.ConfirmWorldAsync(id, B.NormalizedPhone, B.ObservedUid, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.ConfirmWorldAsync(id, B.NormalizedPhone, B.ObservedUid, default));
        Assert.False((await driver.RestartToLoginAsync(Policy, default)).Ok);
        Assert.Equal(7, desktop.ReadCalls);
        Assert.Equal(4, desktop.Actions.Count);
    }

    [Fact]
    public async Task AVerifiedWorldCanBeRecheckedOnlyForItsBoundAccount()
    {
        var world = Frame(GenshinView.World, uid: B.ObservedUid);
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone), Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Gate), world, world, world, world, world);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);
        var id = Guid.NewGuid();
        Assert.True((await driver.SwitchAsync(B, Policy, default)).Ok);

        var first = await driver.ConfirmWorldAsync(id, B.NormalizedPhone, B.ObservedUid, default);
        var second = await driver.ConfirmWorldAsync(id, B.NormalizedPhone, B.ObservedUid, default);
        Assert.Equal(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.ConfirmWorldAsync(Guid.NewGuid(), B.NormalizedPhone, B.ObservedUid, default));
        Assert.Equal(8, desktop.ReadCalls);
    }

    [Fact]
    public async Task ANewlyReadableUidCannotBeThePreviousAccount()
    {
        var target = B with { ObservedUid = null };
        var world = Frame(GenshinView.World);
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone), Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Gate), world, world, world with { Uid = B.PreviousUid });
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);
        Assert.True((await driver.SwitchAsync(target, Policy, default)).Ok);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            driver.ConfirmWorldAsync(Guid.NewGuid(), B.NormalizedPhone, null, default));
    }

    [Fact]
    public async Task AUidReadAtHandoffStillGuardsTheNextSwitch()
    {
        var firstUid = B.PreviousUid;
        var firstWorld = Frame(GenshinView.World, uid: firstUid);
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Gate), Frame(GenshinView.World), Frame(GenshinView.World), firstWorld,
            firstWorld, Frame(GenshinView.Menu), Frame(GenshinView.LeaveWorld), Frame(GenshinView.Gate),
            Frame(GenshinView.ConfirmLogout), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Card, A.NormalizedPhone), Frame(GenshinView.Dropdown, A.NormalizedPhone, B.NormalizedPhone),
            Frame(GenshinView.Card, B.NormalizedPhone), Frame(GenshinView.Gate), firstWorld, firstWorld);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);
        var first = await driver.SwitchAsync(A, Policy, default);
        Assert.True(first.Ok);
        Assert.Null(first.Uid);
        Assert.Equal(firstUid, (await driver.ConfirmWorldAsync(Guid.NewGuid(), A.NormalizedPhone, null, default)).Uid);
        Assert.True((await driver.RestartToLoginAsync(Policy, default)).Ok);

        var second = await driver.SwitchAsync(B with { ObservedUid = null, PreviousUid = null }, Policy, default);

        Assert.False(second.Ok);
        Assert.Equal("mismatch", second.Evidence?.Verdict);
    }

    [Fact]
    public async Task ReturningForTheNextAccountRequiresSeeingThePreviousWorldBeforeLogout()
    {
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Gate), Frame(GenshinView.World), Frame(GenshinView.World),
            Frame(GenshinView.Card, B.NormalizedPhone));
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero);
        Assert.True((await driver.SwitchAsync(A, Policy, default)).Ok);

        var returned = await driver.RestartToLoginAsync(Policy, default);

        Assert.False(returned.Ok);
        Assert.Equal(4, desktop.Actions.Count);
    }

    [Fact]
    public async Task DuplicateDropdownRowsNeverGetClicked()
    {
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone, B.NormalizedPhone));
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).SwitchAsync(B, Policy, default);
        Assert.False(result.Ok);
        Assert.True(result.Fatal);
        Assert.Equal([GenshinInput.OpenDropdown], desktop.Actions);
    }

    [Fact]
    public async Task WorldOnReplacedProcessCannotProveIdentity()
    {
        var replacement = Frame(GenshinView.World) with { Window = Window with { ProcessId = 123 } };
        var desktop = new Desktop(Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Dropdown, A.NormalizedPhone), Frame(GenshinView.Card, A.NormalizedPhone),
            Frame(GenshinView.Gate), replacement);
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).SwitchAsync(A, Policy, default);
        Assert.False(result.Ok);
        Assert.True(result.Fatal);
    }

    [Fact]
    public async Task PreviousWorldUidCannotPassForAnotherAccount()
    {
        var desktop = new Desktop(Frame(GenshinView.Card, B.NormalizedPhone),
            Frame(GenshinView.Dropdown, B.NormalizedPhone), Frame(GenshinView.Card, B.NormalizedPhone), Frame(GenshinView.Gate),
            Frame(GenshinView.World, uid: B.PreviousUid), Frame(GenshinView.World, uid: B.PreviousUid));
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).SwitchAsync(B, Policy, default);
        Assert.False(result.Ok);
        Assert.Equal("mismatch", result.Evidence?.Verdict);
    }

    [Fact]
    public async Task VerificationPromptStopsWithoutInput()
    {
        var desktop = new Desktop(Frame(GenshinView.NeedsUser));
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).EnsureLoginAsync(Policy, default);
        Assert.True(result.NeedsUser);
        Assert.Empty(desktop.Actions);
    }

    [Fact]
    public async Task ColdStartFailureStopsBeforeReadingOrClickingTheDesktop()
    {
        var desktop = new Desktop { PrepareError = "原神启动后没有出现受控窗口。" };
        var result = await new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero).EnsureLoginAsync(Policy, default);

        Assert.False(result.Ok);
        Assert.True(result.Fatal);
        Assert.Equal(desktop.PrepareError, result.Detail);
        Assert.Equal(1, desktop.PrepareCalls);
        Assert.Empty(desktop.Actions);
    }

    [Theory]
    [InlineData(true, true, true, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(false, false, true, 0)]
    public async Task OnlyTaskBatchesRequireConfirmedDesktopCleanup(bool stopOwnedClient, bool exited,
        bool expectedOk, int expectedStopCalls)
    {
        var desktop = new Desktop
        {
            StopResult = exited
                ? LoginSwitchOutcome.Success(LoginSwitchStage.Done, "已确认原神退出。")
                : LoginSwitchOutcome.Fail(LoginSwitchStage.Failed, "原神退出未确认。") with { Fatal = true },
        };
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.Zero, stopOwnedClient);

        var stopped = await driver.StopGameAsync(default);

        Assert.Equal(expectedOk, stopped.Ok);
        Assert.Equal(expectedStopCalls, desktop.StopCalls);
        if (stopOwnedClient)
            Assert.Equal(desktop.StopResult, stopped);
    }

    [Fact]
    public void TemplateMatcherRequiresUniqueVisualEvidence()
    {
        var icon = new CapturedFrame(new byte[18 * 12 * 4], 18, 12, "test-icon");
        var random = new Random(490);
        random.NextBytes(icon.Bgra);
        for (var i = 3; i < icon.Bgra.Length; i += 4) icon.Bgra[i] = 255;
        var frame = new CapturedFrame(new byte[80 * 1080 * 4], 80, 1080, "test-screen");
        var region = new ScreenRegion(0, 0, 80, 30);
        Assert.Null(GenshinLoginTemplates.Match(frame, icon, region, default));
        Paste(5);
        Assert.Equal(new ScreenRegion(5, 8, 18, 12), GenshinLoginTemplates.Match(frame, icon, region, default));
        Paste(40);
        Assert.Null(GenshinLoginTemplates.Match(frame, icon, region, default));

        void Paste(int x)
        {
            for (var row = 0; row < icon.Height; row++)
                Buffer.BlockCopy(icon.Bgra, row * icon.Stride, frame.Bgra, (row + 8) * frame.Stride + x * 4, icon.Stride);
        }
    }

    private static GenshinDesktopFrame Frame(GenshinView view, string? mask = null, string? second = null, string? uid = null)
    {
        var region = new ScreenRegion(1, 1, 4, 4);
        var rows = new[] { mask, second }.OfType<string>().Select((phone, i) =>
            new LoginAccountObservation(phone, phone, i, 3, i * 20, 20, 15, .9, "")).ToArray();
        return new(Window, view, rows, region, region, uid);
    }

    private sealed class Desktop(params GenshinDesktopFrame[] frames) : IGenshinLoginDesktop
    {
        private readonly Queue<GenshinDesktopFrame> _frames = new(frames);
        public List<GenshinInput> Actions { get; } = [];
        public int PrepareCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public StarRailWindowInfo? RestoredWindow { get; private set; }
        public void RestoreForeground(StarRailWindowInfo window) => RestoredWindow = window;
        public Task RestartOwnedClientAsync(TimeSpan timeout, CancellationToken ct)
        {
            RestartCalls++;
            return Task.CompletedTask;
        }
        public string? PrepareError { get; init; }
        public LoginSwitchOutcome? StopResult { get; init; }
        public Task PrepareAsync(TimeSpan timeout, CancellationToken ct)
        {
            PrepareCalls++;
            return PrepareError is null ? Task.CompletedTask : Task.FromException(new InvalidOperationException(PrepareError));
        }
        public Task<GenshinDesktopFrame> ReadAsync(CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(_frames.Dequeue());
        }
        public bool TryInput(GenshinDesktopFrame frame, GenshinInput input, ScreenRegion? point)
        {
            Actions.Add(input);
            return true;
        }
        public LoginSwitchOutcome StopOwnedClient()
        {
            StopCalls++;
            return StopResult ?? throw new InvalidOperationException("本测试不应请求清理原神客户端。");
        }
    }
}
