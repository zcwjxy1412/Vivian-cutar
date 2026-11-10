using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal enum GenshinView
{
    Unknown, CheckingUpdates, NeedsUser, Card, Dropdown, Gate, World, Menu, LeaveWorld, ConfirmLogout,
}

internal enum GenshinInput
{
    EnterAccount, OpenDropdown, ChooseAccount, OpenGate, OpenMenu, LeaveMenu, ConfirmLeave, LogoutGate, ConfirmLogout,
}

internal sealed record GenshinDesktopFrame(StarRailWindowInfo? Window, GenshinView View,
    IReadOnlyList<LoginAccountObservation> Rows, ScreenRegion? Main, ScreenRegion? Secondary = null, string? Uid = null,
    string? UserActionMarker = null, LoginScanFailure? CaptureFailure = null, string? VisualDiagnostic = null);

internal sealed record GenshinVerifiedWorld(Guid AccountId, IntPtr WindowHandle, int ProcessId,
    DateTimeOffset ProcessStartedAt, string? Uid);

internal interface IGenshinLoginDesktop
{
    Task PrepareAsync(TimeSpan timeout, CancellationToken ct);
    Task<GenshinDesktopFrame> ReadAsync(CancellationToken ct);
    bool TryInput(GenshinDesktopFrame frame, GenshinInput input, ScreenRegion? point);
    LoginSwitchOutcome StopOwnedClient();
}

public sealed class GenshinDesktopLoginDriver : IGenshinLoginSwitchDriver
{
    // Cold launch includes the game's own update check. Returning from a world uses the regular login timeout.
    private static readonly TimeSpan ColdLoginScreenTimeout = TimeSpan.FromMinutes(3);
    private readonly IGenshinLoginDesktop _desktop;
    private readonly TimeSpan _poll;
    private readonly bool _stopOwnedClient;
    private StarRailWindowInfo? _window;
    private ConfirmedLogin? _verifiedLogin;
    private string? _previousWorldUid;
    private bool _closed;

    internal GenshinDesktopLoginDriver(IGenshinLoginDesktop desktop, TimeSpan poll, bool stopOwnedClient = false)
    {
        _desktop = desktop;
        _poll = poll;
        _stopOwnedClient = stopOwnedClient;
    }

    /// <summary>Runs a login-only operation on the configured CN client under the shared desktop lease.</summary>
    public static Task<T> WithClientAsync<T>(string betterGiRoot, string gameExecutable,
        string profileDirectory, Func<IGenshinLoginSwitchDriver, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(operation);
        return WithClientCoreAsync(betterGiRoot, gameExecutable, profileDirectory, null, false, operation, ct);
    }

    /// <summary>The caller holds the same desktop lease until login, task execution and cleanup have all ended.</summary>
    internal static Task<T> WithClientAsync<T>(string betterGiRoot, string gameExecutable,
        DesktopOperationLease lease, bool stopOwnedClient,
        Func<GenshinDesktopLoginDriver, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(operation);
        return WithClientCoreAsync(betterGiRoot, gameExecutable, null, lease, stopOwnedClient, operation, ct);
    }

    private static async Task<T> WithClientCoreAsync<T>(string betterGiRoot, string gameExecutable,
        string? profileDirectory, DesktopOperationLease? externalLease, bool stopOwnedClient,
        Func<GenshinDesktopLoginDriver, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("Windows 未授予原神自动登录所需的管理员权限。");
        if (!Path.IsPathFullyQualified(gameExecutable) ||
            !string.Equals(Path.GetFileName(gameExecutable), "YuanShen.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(gameExecutable) || ReadOnlyFileAccess.FindReparsePoint(gameExecutable) is not null)
            throw new InvalidOperationException("请指定已安装的原神国服 YuanShen.exe。");
        var gamePath = Path.GetFullPath(gameExecutable);
        var install = await BetterGiInstallationInspector.InspectAsync(betterGiRoot, ct).ConfigureAwait(false);
        if (install.ProgramPath is not { } program || install.DetectedVersion != BetterGiContract.PinnedTag ||
            install.Compatibility != CompatibilityClass.PendingValidation)
            throw new InvalidOperationException("未找到受支持的 BetterGI 安装版本。");
        if (externalLease?.Recovering == true)
            throw new InvalidOperationException("上次任务仍待恢复，无法在恢复期间操作原神账号。");
        using var ownedLease = externalLease is null ? DesktopOperationLease.Acquire(profileDirectory!) : null;
        if (new BetterGiProcessProbe().IsBusy(program))
            throw new InvalidOperationException("请退出 BetterGI，再验收原神账号切换。");
        var templates = await GenshinLoginTemplates.LoadAsync(Path.GetDirectoryName(program)!, ct).ConfigureAwait(false);
        var desktop = new GenshinWindowsLoginDesktop(gamePath, program, templates, stopOwnedClient);
        var driver = new GenshinDesktopLoginDriver(desktop, TimeSpan.FromMilliseconds(450), stopOwnedClient);
        try
        {
            return await operation(driver, ct).ConfigureAwait(false);
        }
        finally
        {
            driver._closed = true;
            Interlocked.Exchange(ref driver._verifiedLogin, null);
            desktop.Close();
        }
    }

    public async Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _verifiedLogin, null);
        _previousWorldUid = null;
        if (_closed)
            return Failed(LoginSwitchStage.ReturningToLogin, "原神桌面会话已结束。");
        try
        {
            await _desktop.PrepareAsync(policy.GameStart, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Failed(LoginSwitchStage.ReturningToLogin, ex.Message);
        }
        return await ReachCardAsync(policy.LoginScreenTimeout is null ? ColdLoginScreenTimeout : policy.LoginScreen,
            null, cancellationToken).ConfigureAwait(false);
    }

    public Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        var verified = Interlocked.Exchange(ref _verifiedLogin, null);
        return verified is null
            ? Task.FromResult(Failed(LoginSwitchStage.ReturningToLogin, "没有可核对的上一账号世界，已停止切换。"))
            : ReachCardAsync(policy.LoginScreen, verified, cancellationToken);
    }

    internal Task<(int Original, int Condensed)> ReadResinInventoryAsync(GenshinVerifiedWorld world,
        GenshinResinInventoryReader reader, CancellationToken ct) => _desktop is GenshinWindowsLoginDesktop native
        ? native.ReadResinInventoryAsync(world, reader, ct)
        : throw new InvalidOperationException("当前原神桌面会话不能核对树脂库存。");

    public Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken)
    {
        _window = null;
        Interlocked.Exchange(ref _verifiedLogin, null);
        _previousWorldUid = null;
        if (_stopOwnedClient)
        {
            if (_closed)
                return Task.FromResult(Failed(LoginSwitchStage.Failed, "原神桌面会话已结束，无法确认客户端退出。"));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_desktop.StopOwnedClient());
        }
        return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "原神输入已结束，客户端保持打开。"));
    }

    public async Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _verifiedLogin, null);
        var phone = GenshinLoginScreen.NormalizeExactMask(target.MaskedPhone);
        if (phone.Length == 0 || phone != target.NormalizedPhone)
            return Failed(LoginSwitchStage.SelectingAccount, "账号没有可核对的原神登录标识。");
        if (_previousWorldUid is not null && target.PreviousUid is not null && _previousWorldUid != target.PreviousUid)
            return Failed(LoginSwitchStage.SelectingAccount, "上一账号的原神世界 UID 证据不一致，已停止切换。");
        var previousUid = _previousWorldUid ?? target.PreviousUid;

        var stage = LoginSwitchStage.SelectingAccount;
        try
        {
            var card = await WaitAsync(policy.Step, view => view.View == GenshinView.Card, cancellationToken);
            Act(card, GenshinInput.OpenDropdown, card.Secondary, cancellationToken);
            var dropdown = await WaitAsync(policy.Step, view => view.View == GenshinView.Dropdown, cancellationToken);
            var matches = dropdown.Rows.Where(row => row.NormalizedPhone == phone).Take(2).ToArray();
            if (matches.Length != 1)
                return Failed(stage, "下拉列表没有唯一匹配的目标账号，已停止。");
            var match = matches[0];
            Act(dropdown, GenshinInput.ChooseAccount, new(match.X, match.Y, match.Width, match.Height), cancellationToken);
            card = await WaitAsync(policy.Step, view => view.View == GenshinView.Card &&
                view.Rows.Single().NormalizedPhone == phone, cancellationToken);

            var confirmedAt = DateTimeOffset.UtcNow;
            stage = LoginSwitchStage.EnteringGame;
            Act(card, GenshinInput.EnterAccount, card.Main, cancellationToken);
            var enterAt = DateTimeOffset.UtcNow;
            var gate = await WaitAsync(policy.Step, view =>
            {
                if (view.View == GenshinView.Card && view.Rows.Single().NormalizedPhone != phone)
                    throw new LoginInterruptedException("登录卡片在进入游戏后变成了其他账号。");
                return view.View == GenshinView.Gate;
            }, cancellationToken);
            Act(gate, GenshinInput.OpenGate, gate.Main, cancellationToken);
            var gateAt = DateTimeOffset.UtcNow;

            stage = LoginSwitchStage.VerifyingSession;
            GenshinDesktopFrame? previous = null;
            var world = await WaitAsync(policy.InGame, view =>
            {
                if (view.View is GenshinView.Card or GenshinView.Dropdown)
                    throw new LoginInterruptedException("进入世界期间返回了账号选择界面。");
                if (view.View != GenshinView.World)
                {
                    previous = null;
                    return false;
                }
                var stable = previous is not null && previous.Uid == view.Uid;
                previous = view;
                return stable;
            }, cancellationToken);
            if (target.ObservedUid is not null && world.Uid is null)
                return Failed(stage, "此档案已有入场 UID 记录，当前画面未能读回核对。");

            var evidence = AccountSwitchVerifier.Judge(new AccountSwitchEvidence
            {
                TargetMasked = target.MaskedPhone,
                PreviousMasked = target.PreviousMasked,
                ConfirmedSelectionMasked = card.Rows.Single().MaskedPhone,
                SelectionConfirmedAt = confirmedAt,
                EnterPressedAt = enterAt,
                TitlePassedAt = gateAt,
                InGameAt = DateTimeOffset.UtcNow,
                Uid = world.Uid,
                PreviousUid = previousUid,
                ExpectedUid = target.ObservedUid,
                Trail = ["目标卡片", "原神大门", "连续世界画面"],
            });
            if (evidence.Verdict != "verified") return Failed(stage, evidence.VerdictDetail) with { Evidence = evidence };
            _verifiedLogin = new(phone, world.Window!, world.Uid, previousUid);
            return LoginSwitchOutcome.Success(LoginSwitchStage.Done, evidence.VerdictDetail, world.Uid) with { Evidence = evidence };
        }
        catch (LoginInterruptedException ex) { return Failed(stage, ex.Message, ex.NeedsUser); }
    }

    /// <summary>Reads a new world frame for the current verified account without sending input.</summary>
    internal async Task<GenshinVerifiedWorld> ConfirmWorldAsync(Guid accountId, string normalizedPhone,
        string? expectedUid, CancellationToken ct)
    {
        var verified = _verifiedLogin;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_closed || accountId == Guid.Empty || verified is null || verified.Phone != normalizedPhone ||
                (verified.AccountId is { } previousAccount && previousAccount != accountId))
                throw new InvalidOperationException("没有与当前任务账号匹配的原神登录验证，已停止。");

            var frame = await _desktop.ReadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (frame.Window is not { ProcessStartedAt: { } started } window || !SameWindow(verified.Window, window))
                throw new InvalidOperationException("原神前台窗口或客户端身份已变化，已停止日常。");
            if (frame.View != GenshinView.World)
                throw new InvalidOperationException("日常前无法确认原神已进入游戏世界，已停止。");
            if (!MatchesUid(verified, frame.Uid) || (expectedUid is not null && expectedUid != frame.Uid))
                throw new InvalidOperationException("日常前无法核对当前原神世界 UID，已停止。");

            var confirmed = verified with { AccountId = accountId, Uid = frame.Uid };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _verifiedLogin, confirmed, verified), verified))
                throw new InvalidOperationException("原神登录验证已变化，已停止日常。");
            return new(accountId, window.Handle, window.ProcessId, started, frame.Uid);
        }
        catch
        {
            Interlocked.CompareExchange(ref _verifiedLogin, null, verified);
            throw;
        }
    }

    private async Task<LoginSwitchOutcome> ReachCardAsync(TimeSpan timeout, ConfirmedLogin? verified, CancellationToken ct)
    {
        const LoginSwitchStage stage = LoginSwitchStage.ReturningToLogin;
        try
        {
            GenshinInput? last = null;
            string? previousUid = null;
            await WaitAsync(timeout, view =>
            {
                if (last is null && verified is not null)
                {
                    if (view.View != GenshinView.World || !MatchesUid(verified, view.Uid))
                        throw new LoginInterruptedException("退出前无法核对上一账号的原神世界，已停止切换。");
                    previousUid = view.Uid;
                }
                if (view.View == GenshinView.Card)
                    return last is null or GenshinInput.ConfirmLogout;
                var input = (last, view.View) switch
                {
                    (null, GenshinView.World) => GenshinInput.OpenMenu,
                    (null or GenshinInput.OpenMenu, GenshinView.Menu) => GenshinInput.LeaveMenu,
                    (GenshinInput.LeaveMenu, GenshinView.LeaveWorld) => GenshinInput.ConfirmLeave,
                    (null or GenshinInput.ConfirmLeave, GenshinView.Gate) => GenshinInput.LogoutGate,
                    (GenshinInput.LogoutGate, GenshinView.ConfirmLogout) => GenshinInput.ConfirmLogout,
                    _ => (GenshinInput?)null,
                };
                if (input is { } action)
                {
                    Act(view, action, action == GenshinInput.LogoutGate ? view.Secondary : view.Main, ct);
                    last = action;
                }
                return false;
            }, ct);
            if (verified is not null) _previousWorldUid = previousUid;
            return LoginSwitchOutcome.Success(stage, "已核对原神保存账号卡片。");
        }
        catch (LoginInterruptedException ex) { return Failed(stage, ex.Message, ex.NeedsUser); }
    }

    private async Task<GenshinDesktopFrame> WaitAsync(TimeSpan timeout, Func<GenshinDesktopFrame, bool> ready, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        GenshinDesktopFrame? last = null;
        try
        {
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                if (_closed) throw new LoginInterruptedException("原神桌面会话已结束。");
                var frame = await _desktop.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
                if (frame.Window is not { ProcessStartedAt: not null } window || window.Handle == IntPtr.Zero ||
                    window.ProcessId <= 0 || _window is { } expected && !SameWindow(expected, window))
                    throw new LoginInterruptedException("原神窗口不在前台，或客户端身份已经变化。");
                _window = window;
                last = frame;
                if (frame.View == GenshinView.NeedsUser)
                    throw new LoginInterruptedException(frame.UserActionMarker is { } marker
                        ? $"原神需要手动完成登录验证或协议确认。画面标志为「{marker}」。"
                        : "原神需要手动完成登录验证或协议确认。", true);
                timeoutSource.Token.ThrowIfCancellationRequested();
                if (ready(frame)) return frame;
                await Task.Delay(_poll, timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (last?.View == GenshinView.CheckingUpdates)
                throw new LoginInterruptedException("等待超时，原神仍在检查更新，尚未到达登录画面。");
            var reason = last?.CaptureFailure switch
            {
                LoginScanFailure.WindowOccluded => "最近一次采集时画面被遮挡。",
                not null => "最近一次采集未获得可核对的游戏画面。",
                _ when last?.View == GenshinView.Unknown => "已取得游戏画面，画面尚未识别。" + last.VisualDiagnostic,
                _ => "",
            };
            throw new LoginInterruptedException("等待原神下一步画面超时，已停止。" + reason);
        }
    }

    private void Act(GenshinDesktopFrame frame, GenshinInput action, ScreenRegion? point, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_desktop.TryInput(frame, action, point))
            throw new LoginInterruptedException("未能核对原神操作位置或前台窗口，已停止。");
    }

    private static LoginSwitchOutcome Failed(LoginSwitchStage stage, string detail, bool needsUser = false) =>
        LoginSwitchOutcome.Fail(stage, detail) with { Fatal = true, NeedsUser = needsUser };

    private static bool SameWindow(StarRailWindowInfo expected, StarRailWindowInfo actual) =>
        expected.Handle == actual.Handle && expected.ProcessId == actual.ProcessId &&
        expected.ProcessStartedAt == actual.ProcessStartedAt;

    private static bool MatchesUid(ConfirmedLogin verified, string? uid) =>
        (verified.Uid is null || verified.Uid == uid) &&
        (uid is null || verified.PreviousUid != uid);

    private sealed record ConfirmedLogin(string Phone, StarRailWindowInfo Window, string? Uid, string? PreviousUid,
        Guid? AccountId = null);

    private sealed class LoginInterruptedException(string message, bool needsUser = false) : Exception(message)
    {
        public bool NeedsUser { get; } = needsUser;
    }
}
