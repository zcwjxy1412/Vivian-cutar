using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal enum GenshinView
{
    Unknown, NeedsUser, Card, Dropdown, Gate, World, Menu, LeaveWorld, ConfirmLogout,
}

internal enum GenshinInput
{
    EnterAccount, OpenDropdown, ChooseAccount, OpenGate, OpenMenu, LeaveMenu, ConfirmLeave, LogoutGate, ConfirmLogout,
}

internal sealed record GenshinDesktopFrame(StarRailWindowInfo? Window, GenshinView View,
    IReadOnlyList<LoginAccountObservation> Rows, ScreenRegion? Main, ScreenRegion? Secondary = null, string? Uid = null);

internal interface IGenshinLoginDesktop
{
    Task PrepareAsync(TimeSpan timeout, CancellationToken ct);
    Task<GenshinDesktopFrame> ReadAsync(CancellationToken ct);
    bool TryInput(GenshinDesktopFrame frame, GenshinInput input, ScreenRegion? point);
}

public sealed class GenshinDesktopLoginDriver : IGenshinLoginSwitchDriver
{
    private readonly IGenshinLoginDesktop _desktop;
    private readonly TimeSpan _poll;
    private StarRailWindowInfo? _window;
    private string? _lastVerifiedUid;

    internal GenshinDesktopLoginDriver(IGenshinLoginDesktop desktop, TimeSpan poll)
    {
        _desktop = desktop;
        _poll = poll;
    }

    /// <summary>Runs a login-only operation on the configured CN client under the shared desktop lease.</summary>
    public static async Task<T> WithClientAsync<T>(string betterGiRoot, string gameExecutable,
        string profileDirectory, Func<IGenshinLoginSwitchDriver, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
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
        using var lease = DesktopOperationLease.Acquire(profileDirectory);
        if (new BetterGiProcessProbe().IsBusy(program))
            throw new InvalidOperationException("请退出 BetterGI，再验收原神账号切换。");
        var templates = await GenshinLoginTemplates.LoadAsync(Path.GetDirectoryName(program)!, ct).ConfigureAwait(false);
        var desktop = new GenshinWindowsLoginDesktop(gamePath, program, templates);
        try
        {
            return await operation(new GenshinDesktopLoginDriver(desktop, TimeSpan.FromMilliseconds(450)), ct).ConfigureAwait(false);
        }
        finally
        {
            desktop.Close();
        }
    }

    public async Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            await _desktop.PrepareAsync(policy.GameStart, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Failed(LoginSwitchStage.ReturningToLogin, ex.Message);
        }
        return await ReachCardAsync(policy.LoginScreen, cancellationToken).ConfigureAwait(false);
    }

    public Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken) =>
        ReachCardAsync(policy.LoginScreen, cancellationToken);

    public Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken)
    {
        _window = null;
        _lastVerifiedUid = null;
        return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "原神输入已结束，客户端保持打开。"));
    }

    public async Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken cancellationToken)
    {
        var phone = GenshinLoginScreen.NormalizeExactMask(target.MaskedPhone);
        if (phone.Length == 0 || phone != target.NormalizedPhone)
            return Failed(LoginSwitchStage.SelectingAccount, "账号没有可核对的原神登录标识。");

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
                PreviousUid = target.PreviousUid,
                ExpectedUid = target.ObservedUid,
                Trail = ["目标卡片", "原神大门", "连续世界画面"],
            });
            if (evidence.Verdict != "verified") return Failed(stage, evidence.VerdictDetail) with { Evidence = evidence };
            _lastVerifiedUid = world.Uid;
            return LoginSwitchOutcome.Success(LoginSwitchStage.Done, evidence.VerdictDetail, world.Uid) with { Evidence = evidence };
        }
        catch (LoginInterruptedException ex) { return Failed(stage, ex.Message, ex.NeedsUser); }
    }

    private async Task<LoginSwitchOutcome> ReachCardAsync(TimeSpan timeout, CancellationToken ct)
    {
        const LoginSwitchStage stage = LoginSwitchStage.ReturningToLogin;
        try
        {
            GenshinInput? last = null;
            await WaitAsync(timeout, view =>
            {
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
                    if (view.View == GenshinView.World && _lastVerifiedUid is not null && _lastVerifiedUid != view.Uid)
                        throw new LoginInterruptedException("退出前的世界 UID 与刚才验证的账号不一致。");
                    Act(view, action, action == GenshinInput.LogoutGate ? view.Secondary : view.Main, ct);
                    last = action;
                }
                return false;
            }, ct);
            return LoginSwitchOutcome.Success(stage, "已核对原神保存账号卡片。");
        }
        catch (LoginInterruptedException ex) { return Failed(stage, ex.Message, ex.NeedsUser); }
    }

    private async Task<GenshinDesktopFrame> WaitAsync(TimeSpan timeout, Func<GenshinDesktopFrame, bool> ready, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                var frame = await _desktop.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
                if (frame.Window is not { ProcessStartedAt: not null } window ||
                    _window is { } expected && (expected.Handle != window.Handle || expected.ProcessId != window.ProcessId ||
                        expected.ProcessStartedAt != window.ProcessStartedAt))
                    throw new LoginInterruptedException("原神窗口不在前台，或客户端身份已经变化。");
                _window = window;
                if (frame.View == GenshinView.NeedsUser)
                    throw new LoginInterruptedException("原神需要手动完成登录验证或协议确认。", true);
                timeoutSource.Token.ThrowIfCancellationRequested();
                if (ready(frame)) return frame;
                await Task.Delay(_poll, timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LoginInterruptedException("等待原神下一步画面超时，已停止。");
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

    private sealed class LoginInterruptedException(string message, bool needsUser = false) : Exception(message)
    {
        public bool NeedsUser { get; } = needsUser;
    }
}
