using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public enum LoginSwitchStage
{
    Idle,
    FindingWindow,
    WaitingForLogin,
    SelectingAccount,
    EnteringGame,
    VerifyingSession,
    ReturningToLogin,
    Done,
    Failed,
}

public sealed record LoginTarget(string NormalizedPhone, string MaskedPhone, int RowOrder, string? ObservedUid);

public sealed record LoginSwitchOutcome(bool Ok, LoginSwitchStage Stage, string Detail, string? Uid = null)
{
    public static LoginSwitchOutcome Fail(LoginSwitchStage stage, string detail) => new(false, stage, detail);
    public static LoginSwitchOutcome Success(LoginSwitchStage stage, string detail, string? uid = null) =>
        new(true, stage, detail, uid);
}

public interface ILoginSwitchDriver
{
    Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken);
    Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken cancellationToken);
    Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken);
    Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Drives the real Star Rail login window. It only uses the supported UI: open the saved-account list, choose the
/// row that matches the registered masked phone, press "进入游戏", then "点击进入". No registry import, token
/// copy, memory write or login-check bypass is used.
/// </summary>
public sealed class StarRailLoginDriver(
    ILoginScreenSource source,
    IGameSessionVerifier verifier,
    IInputHost input,
    Func<string> gamePath,
    Action<string>? log = null) : ILoginSwitchDriver
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(1500);

    public async Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        log?.Invoke("ensure-login");
        if (StarRailWindow.Find() is null)
        {
            log?.Invoke("game-not-running; starting");
            var started = await input.StartGameAsync(gamePath(), cancellationToken).ConfigureAwait(false);
            if (!started.Ok) return LoginSwitchOutcome.Fail(LoginSwitchStage.FindingWindow, started.Error ?? "无法启动星铁。");
        }
        var deadline = DateTimeOffset.UtcNow + policy.GameStart;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (view.LoginDialogVisible) return LoginSwitchOutcome.Success(LoginSwitchStage.WaitingForLogin, "登录界面已出现。");
            // The title screen shows "点击进入" before the account dialog. Click it, then keep polling:
            // a saved session may auto-enter the world, in which case the caller has to log out explicitly.
            var window = view.Window ?? StarRailWindow.Find();
            if (window is not null && view.Result.Success &&
                await TryClickClickEnterAsync(window, view, cancellationToken).ConfigureAwait(false)) continue;
            if (await TryHandleTransientAsync(view, cancellationToken).ConfigureAwait(false)) continue;
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
        return LoginSwitchOutcome.Fail(LoginSwitchStage.WaitingForLogin,
            $"等待星铁登录界面超时（{policy.GameStart.TotalSeconds:0} 秒）。" );
    }

    public async Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy,
        CancellationToken cancellationToken)
    {
        var login = await EnsureLoginAsync(policy, cancellationToken).ConfigureAwait(false);
        if (!login.Ok) return login;
        var view = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
        var window = view.Window ?? StarRailWindow.Find();
        if (window is null) return LoginSwitchOutcome.Fail(LoginSwitchStage.FindingWindow, "没有找到星铁游戏窗口。");
        if (!view.LoginDialogVisible)
            return LoginSwitchOutcome.Fail(LoginSwitchStage.WaitingForLogin, view.Result.Detail);

        var selection = Choose(view, target);
        if (!selection.Success)
        {
            // A collapsed combo box shows only the current account. Open it by clicking the visible row.
            if (!await ClickRowAsync(window, view, cancellationToken).ConfigureAwait(false))
                return LoginSwitchOutcome.Fail(LoginSwitchStage.SelectingAccount,
                    selection.Detail + " 无法展开账号列表。");
            view = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
            selection = Choose(view, target);
        }
        if (!selection.Success)
        {
            var arrow = await ClickArrowAsync(window, view, cancellationToken).ConfigureAwait(false);
            if (!arrow) return LoginSwitchOutcome.Fail(LoginSwitchStage.SelectingAccount, selection.Detail);
            view = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
            selection = Choose(view, target);
        }
        if (!selection.Success) return LoginSwitchOutcome.Fail(LoginSwitchStage.SelectingAccount, selection.Detail);

        var account = selection.Account!;
        if (account.RowOrder != 0)
        {
            log?.Invoke($"select-row {account.RowOrder} {account.NormalizedPhone}");
            var click = await input.ClickAsync(window, (int)account.CenterX, (int)account.CenterY, cancellationToken)
                .ConfigureAwait(false);
            if (!click.Ok) return LoginSwitchOutcome.Fail(LoginSwitchStage.SelectingAccount, click.Error ?? "选择账号失败。");
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            view = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
        }

        log?.Invoke("press-enter-game");
        if (!await ClickTextAsync(window, view, "进入游戏", cancellationToken).ConfigureAwait(false))
        {
            var panel = view.Panel;
            if (panel is null)
                return LoginSwitchOutcome.Fail(LoginSwitchStage.EnteringGame, "没有找到“进入游戏”按钮。");
            var fallback = await input.ClickAsync(window, (int)panel.CenterX, (int)(panel.Y + panel.Height * 0.60),
                cancellationToken).ConfigureAwait(false);
            if (!fallback.Ok) return LoginSwitchOutcome.Fail(LoginSwitchStage.EnteringGame, fallback.Error ?? "点击“进入游戏”失败。");
        }

        var deadline = DateTimeOffset.UtcNow + policy.LoginScreen;
        var lastDetail = "等待登录完成。";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            view = await source.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (await TryClickClickEnterAsync(window, view, cancellationToken).ConfigureAwait(false)) continue;
            if (await TryHandleTransientAsync(view, cancellationToken).ConfigureAwait(false)) continue;
            if (view.Result.Success && view.LoginDialogVisible)
            {
                lastDetail = "登录对话框仍未关闭。";
                await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var verify = await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (verify.InGame)
            {
                if (target.ObservedUid is { Length: > 0 } expected && verify.Uid is { Length: > 0 } actual &&
                    !string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    return LoginSwitchOutcome.Fail(LoginSwitchStage.VerifyingSession,
                        $"登录后的 UID {actual} 与档案记录的 {expected} 不一致，已停止执行。");
                }
                return LoginSwitchOutcome.Success(LoginSwitchStage.Done,
                    verify.Detail + (verify.Uid is null ? "" : $" UID {verify.Uid}。"), verify.Uid);
            }
            lastDetail = verify.Detail;
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
        return LoginSwitchOutcome.Fail(LoginSwitchStage.VerifyingSession,
            $"等待进入游戏超时（{policy.LoginScreen.TotalSeconds:0} 秒）：{lastDetail}");
    }

    public async Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        var stop = await StopGameAsync(cancellationToken).ConfigureAwait(false);
        if (!stop.Ok) return stop;
        var started = await input.StartGameAsync(gamePath(), cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return LoginSwitchOutcome.Fail(LoginSwitchStage.FindingWindow, started.Error ?? "无法重启星铁。");
        return await EnsureLoginAsync(policy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken)
    {
        var window = StarRailWindow.Find();
        if (window is null)
        {
            if (!StarRailWindow.IsRunning) return LoginSwitchOutcome.Success(LoginSwitchStage.Done, "游戏未运行。");
            return LoginSwitchOutcome.Fail(LoginSwitchStage.Failed, "游戏进程存在但没有可识别的窗口，无法安全结束。");
        }
        var kill = await input.KillAsync(window.ProcessId, cancellationToken).ConfigureAwait(false);
        if (!kill.Ok) return LoginSwitchOutcome.Fail(LoginSwitchStage.Failed, kill.Error ?? "结束游戏进程失败。");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!StarRailWindow.IsRunning) return LoginSwitchOutcome.Success(LoginSwitchStage.Done, "游戏进程已退出。");
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return LoginSwitchOutcome.Fail(LoginSwitchStage.Failed, "游戏进程在 12 秒内未退出，已停止后续切换。");
    }

    private static AccountSelection Choose(LoginScreenView view, LoginTarget target)
    {
        var accounts = view.Result.Observation?.Accounts;
        if (accounts is null || accounts.Count == 0)
            return new AccountSelection(false, -1, null, AccountSelectionFailure.NotFound, "登录界面没有读到已保存账号。");
        return AccountSelectionResolver.Resolve(accounts, target.NormalizedPhone, target.RowOrder);
    }

    private async Task<bool> ClickRowAsync(StarRailWindowInfo window, LoginScreenView view, CancellationToken cancellationToken)
    {
        var row = view.Result.Observation?.Accounts.FirstOrDefault();
        if (row is null) return false;
        var click = await input.ClickAsync(window, (int)row.CenterX, (int)row.CenterY, cancellationToken).ConfigureAwait(false);
        if (!click.Ok) return false;
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClickArrowAsync(StarRailWindowInfo window, LoginScreenView view, CancellationToken cancellationToken)
    {
        var panel = view.Panel;
        var row = view.Result.Observation?.Accounts.FirstOrDefault();
        if (panel is null || row is null) return false;
        var x = (int)Math.Round(panel.Right - 72);
        var click = await input.ClickAsync(window, x, (int)row.CenterY, cancellationToken).ConfigureAwait(false);
        if (!click.Ok) return false;
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ClickTextAsync(StarRailWindowInfo window, LoginScreenView view, string text,
        CancellationToken cancellationToken)
    {
        var line = view.Lines
            .Where(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c)))
                .Contains(text, StringComparison.Ordinal))
            .OrderByDescending(l => l.Width * l.Height)
            .FirstOrDefault();
        if (line is null) return false;
        var click = await input.ClickAsync(window, (int)line.CenterX, (int)line.CenterY, cancellationToken).ConfigureAwait(false);
        return click.Ok;
    }

    private async Task<bool> TryClickClickEnterAsync(StarRailWindowInfo window, LoginScreenView view,
        CancellationToken cancellationToken)
    {
        if (!await ClickTextAsync(window, view, "点击进入", cancellationToken).ConfigureAwait(false)) return false;
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryHandleTransientAsync(LoginScreenView view, CancellationToken cancellationToken)
    {
        var window = view.Window ?? StarRailWindow.Find();
        if (window is null) return false;
        var lines = view.Lines;
        foreach (var keyword in new[] { "我知道了", "同意", "确认", "关闭" })
        {
            if (!await ClickTextAsync(window, view, keyword, cancellationToken).ConfigureAwait(false)) continue;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            return true;
        }
        var compact = lines.Select(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c)))).ToArray();
        if (compact.Any(t => t.Contains("游戏前详阅", StringComparison.Ordinal) ||
                             t.Contains("警告", StringComparison.Ordinal)))
        {
            var click = await input.ClickAsync(window, window.ClientWidth / 2, window.ClientHeight / 2,
                cancellationToken).ConfigureAwait(false);
            if (click.Ok) await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            return click.Ok;
        }
        return false;
    }
}
