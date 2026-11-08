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

/// <param name="PreviousMasked">Masked phone of the account that ran just before, if any (evidence only).</param>
/// <param name="PreviousUid">UID read for the previous account; the new session must not show the same UID.</param>
public sealed record LoginTarget(string NormalizedPhone, string MaskedPhone, int RowOrder, string? ObservedUid,
    string? PreviousMasked = null, string? PreviousUid = null);

public sealed record LoginSwitchOutcome(bool Ok, LoginSwitchStage Stage, string Detail, string? Uid = null)
{
    /// <summary>The batch must stop: identity is uncertain, the user must act, or the screen did not react.</summary>
    public bool Fatal { get; init; }
    /// <summary>Password, verification code, agreement or another step only the user may complete.</summary>
    public bool NeedsUser { get; init; }
    public AccountSwitchEvidence? Evidence { get; init; }
    public IReadOnlyList<string> Trail { get; init; } = [];

    public static LoginSwitchOutcome Fail(LoginSwitchStage stage, string detail) => new(false, stage, detail);
    public static LoginSwitchOutcome Success(LoginSwitchStage stage, string detail, string? uid = null) =>
        new(true, stage, detail, uid);
}

public interface ILoginSwitchDriver
{
    /// <summary>Reaches the saved-account card or list without entering the world as any account.</summary>
    Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken);
    Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken cancellationToken);
    Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken);
    Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Drives the real Star Rail login screens through a state machine (<see cref="LoginFlowSession"/>): every click is
/// derived from text visible in the current frame, unknown screens are only waited on, per-state click counts and
/// step counts are bounded, and authentication or consent screens stop the batch for the user. To reach the account
/// list it logs out from the title screen instead of pressing "点击进入", which would enter as the previous account.
/// No registry import, token copy, memory read or login-check bypass is used.
/// </summary>
public sealed class StarRailLoginDriver : ILoginSwitchDriver
{
    private readonly ILoginScreenSource _source;
    private readonly IGameSessionVerifier _verifier;
    private readonly IInputHost _input;
    private readonly Func<string> _gamePath;
    private readonly Action<string>? _log;
    private readonly IGameWindowLocator _locator;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly LoginFlowBudget _budget;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IDisposable?>? _prepareLaunch;
    private readonly Func<LoginScreenView, string, Task>? _saveEvidence;

    public static TimeSpan Poll { get; } = TimeSpan.FromMilliseconds(1500);

    public StarRailLoginDriver(ILoginScreenSource source, IGameSessionVerifier verifier, IInputHost input,
        Func<string> gamePath, Action<string>? log = null, IGameWindowLocator? locator = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, LoginFlowBudget? budget = null,
        Func<DateTimeOffset>? now = null, Func<IDisposable?>? prepareLaunch = null,
        Func<LoginScreenView, string, Task>? saveEvidence = null)
    {
        _saveEvidence = saveEvidence;
        _prepareLaunch = prepareLaunch;
        _source = source;
        _verifier = verifier;
        _input = input;
        _gamePath = gamePath;
        _log = log;
        _locator = locator ?? StarRailWindowLocator.Instance;
        _delay = delay ?? Task.Delay;
        _budget = budget ?? LoginFlowBudget.Default;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        _log?.Invoke("ensure-account-selectable");
        if (_locator.Find() is null && !_locator.IsProcessRunning())
        {
            _log?.Invoke("game-not-running; starting");
            var started = await LaunchGameAsync(cancellationToken).ConfigureAwait(false);
            if (!started.Ok)
                return Fatal(LoginSwitchStage.FindingWindow, started.Error ?? "无法启动星铁。");
        }
        var session = new LoginFlowSession(LoginGoal.AccountSelectable, _budget);
        var (outcome, _) = await RunFlowAsync(session, policy.GameStart, null, cancellationToken).ConfigureAwait(false);
        return outcome with { Stage = outcome.Ok ? LoginSwitchStage.WaitingForLogin : outcome.Stage };
    }

    public async Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy,
        CancellationToken cancellationToken)
    {
        var evidence = new AccountSwitchEvidence
        {
            TargetMasked = target.MaskedPhone, PreviousMasked = target.PreviousMasked,
            PreviousUid = target.PreviousUid, ExpectedUid = target.ObservedUid,
        };
        var login = await EnsureLoginAsync(policy, cancellationToken).ConfigureAwait(false);
        if (!login.Ok) return login with { Evidence = Judge(evidence, login.Trail) };

        // ---- select the target and prove the collapsed card shows it ----
        var (observation, view) = await ObserveAsync(cancellationToken).ConfigureAwait(false);
        var trail = new List<string>(login.Trail) { Describe(observation) };
        var confirmed = false;
        LoginAccountObservation? collapsedHeader = null;
        for (var attempt = 0; attempt < 4 && !confirmed; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.State is not (LoginScreenState.LoginCard or LoginScreenState.AccountList))
                return Fatal(LoginSwitchStage.SelectingAccount,
                    $"选择账号时画面变为“{LoginFlowSession.Describe(observation.State)}”：{observation.Detail}") with
                    { Evidence = Judge(evidence, trail), Trail = trail };

            var normalized = MaskedPhoneParser.Normalize(target.NormalizedPhone);
            if (observation.State == LoginScreenState.LoginCard && observation.Accounts.Count == 1 &&
                observation.Accounts[0].NormalizedPhone == normalized)
            {
                confirmed = true;
                evidence = evidence with
                {
                    ConfirmedSelectionMasked = observation.Accounts[0].MaskedPhone, SelectionConfirmedAt = _now(),
                };
                break;
            }

            var resolved = AccountSelectionResolver.Resolve(observation.Accounts, target.NormalizedPhone, target.RowOrder);
            if (resolved.Failure == AccountSelectionFailure.Ambiguous)
                return Fatal(LoginSwitchStage.SelectingAccount, resolved.Detail) with
                    { Evidence = Judge(evidence, trail), Trail = trail };

            var window = view.Window ?? _locator.Find();
            if (window is null) return Fatal(LoginSwitchStage.FindingWindow, "选择账号时游戏窗口消失。");

            if (resolved.Success && observation.State == LoginScreenState.AccountList)
            {
                var row = resolved.Account!;
                _log?.Invoke($"select-row {row.RowOrder} {row.NormalizedPhone}");
                if (!await ClickAsync(window, row.CenterX, row.CenterY, cancellationToken)
                        .ConfigureAwait(false))
                    return Fatal(LoginSwitchStage.SelectingAccount, "点击目标账号失败。");
            }
            else if (observation.State == LoginScreenState.AccountList)
            {
                // The list is open and the target is not in it: the saved login was removed. Not fatal for the
                // batch (nothing has been entered), but this account cannot run.
                return LoginSwitchOutcome.Fail(LoginSwitchStage.SelectingAccount,
                    $"已保存账号列表中没有“{target.MaskedPhone}”（可能已被移除），本账号跳过。") with
                    { Evidence = Judge(evidence, trail), Trail = trail };
            }
            else
            {
                // Collapsed card showing another account: open the drop-down. The first try clicks the visible row,
                // the second the arrow at the right edge of the same row. Both are bounded by the attempt loop.
                var row = observation.Accounts.FirstOrDefault();
                if (row is null) return Fatal(LoginSwitchStage.SelectingAccount, "登录卡片上没有可识别的账号行。");
                collapsedHeader = row;
                var x = row.CenterX;
                _log?.Invoke($"open-account-list attempt {attempt + 1}");
                if (!await ClickAsync(window, x, row.CenterY, cancellationToken).ConfigureAwait(false))
                    return Fatal(LoginSwitchStage.SelectingAccount, "无法展开已保存账号列表。");
            }
            await _delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
            (observation, view) = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            if (observation.State == LoginScreenState.AccountList && view.Panel is { } panel && collapsedHeader is not null)
                observation = observation with { Accounts = LoginScreenAnalyzer.SavedRows(view.Lines,
                    new ScreenRegion(panel.X, panel.Y, panel.Width, panel.Height), collapsedHeader) };
            trail.Add(Describe(observation));
        }
        if (!confirmed)
            return Fatal(LoginSwitchStage.SelectingAccount,
                $"无法确认登录卡片已选中“{target.MaskedPhone}”，未按“进入游戏”。") with
                { Evidence = Judge(evidence, trail), Trail = trail };

        // ---- enter the game with the confirmed account ----
        var enter = observation.ActionTarget;
        var selectedWindow = view.Window ?? _locator.Find();
        if (enter is null || selectedWindow is null)
            return Fatal(LoginSwitchStage.EnteringGame, "没有识别到“进入游戏”按钮，未进行任何点击。") with
                { Evidence = Judge(evidence, trail), Trail = trail };
        _log?.Invoke("press-enter-game");
        if (!await ClickAsync(selectedWindow, enter.CenterX, enter.CenterY, cancellationToken).ConfigureAwait(false))
            return Fatal(LoginSwitchStage.EnteringGame, "点击“进入游戏”失败。") with
                { Evidence = Judge(evidence, trail), Trail = trail };
        evidence = evidence with { EnterPressedAt = _now() };

        var world = new LoginFlowSession(LoginGoal.InWorld, _budget);
        var (entered, titlePassedAt) = await RunFlowAsync(world, policy.LoginScreen, selectedWindow, cancellationToken,
            MaskedPhoneParser.Normalize(target.NormalizedPhone))
            .ConfigureAwait(false);
        trail.AddRange(entered.Trail);
        evidence = evidence with
        {
            TitlePassedAt = titlePassedAt,
            RestartAfterSelection = !_locator.IsSameProcessAlive(selectedWindow),
        };
        if (!entered.Ok)
            return entered with { Stage = LoginSwitchStage.VerifyingSession, Fatal = true,
                Evidence = Judge(evidence, trail), Trail = trail };

        // ---- verify the session belongs to the selected account ----
        SessionVerification verify = new(false, null, 0, "尚未校验。");
        var deadline = _now() + policy.InGame;
        while (_now() < deadline)
        {
            verify = await _verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (verify.InGame && verify.Uid is not null) break;
            if (verify.InGame && _now() + Poll >= deadline) break;
            await _delay(Poll, cancellationToken).ConfigureAwait(false);
        }
        if (!verify.InGame)
            return Fatal(LoginSwitchStage.VerifyingSession, "进入游戏后无法确认游戏世界：" + verify.Detail) with
                { Evidence = Judge(evidence, trail), Trail = trail };
        evidence = evidence with { InGameAt = _now(), Uid = verify.Uid,
            RestartAfterSelection = evidence.RestartAfterSelection || !_locator.IsSameProcessAlive(selectedWindow) };
        var judged = Judge(evidence, trail);
        if (judged.Verdict != "verified")
            return Fatal(LoginSwitchStage.VerifyingSession, judged.VerdictDetail + " 已停止，不会在不确定的账号上执行任务。") with
                { Evidence = judged, Trail = trail };
        return LoginSwitchOutcome.Success(LoginSwitchStage.Done, judged.VerdictDetail, verify.Uid) with
            { Evidence = judged, Trail = trail };
    }

    public async Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken cancellationToken)
    {
        var stop = await StopGameAsync(cancellationToken).ConfigureAwait(false);
        if (!stop.Ok) return stop with { Fatal = true };
        var started = await LaunchGameAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Ok) return Fatal(LoginSwitchStage.FindingWindow, started.Error ?? "无法重启星铁。");
        return await EnsureLoginAsync(policy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginSwitchOutcome> StopGameAsync(CancellationToken cancellationToken)
    {
        var window = _locator.Find();
        if (window is null)
        {
            if (!_locator.IsProcessRunning()) return LoginSwitchOutcome.Success(LoginSwitchStage.Done, "游戏未运行。");
            return Fatal(LoginSwitchStage.Failed, "星铁进程存在但没有可识别的窗口，无法证明进程身份，未结束它。");
        }
        var kill = await _input.KillAsync(window, cancellationToken).ConfigureAwait(false);
        if (!kill.Ok) return Fatal(LoginSwitchStage.Failed, kill.Error ?? "结束游戏进程失败。");
        var deadline = _now() + TimeSpan.FromSeconds(12);
        while (_now() < deadline)
        {
            if (!_locator.IsSameProcessAlive(window))
                return LoginSwitchOutcome.Success(LoginSwitchStage.Done, "游戏进程已退出（按进程号与启动时间核实）。");
            await _delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }
        return Fatal(LoginSwitchStage.Failed, "游戏进程在 12 秒内未退出，已停止后续切换。");
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the game the way March7thAssistant would: launch settings applied just before the process starts and
    /// restored ten seconds after its window appears (upstream waits the same), or when the launch fails.
    /// </summary>
    private async Task<InputResponse> LaunchGameAsync(CancellationToken cancellationToken)
    {
        using var prepared = _prepareLaunch?.Invoke();
        var started = await _input.StartGameAsync(_gamePath(), cancellationToken).ConfigureAwait(false);
        if (!started.Ok || prepared is null) return started;
        var deadline = _now() + TimeSpan.FromSeconds(120);
        while (_now() < deadline && _locator.Find() is null)
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        await _delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return started;
    }

    private async Task<(LoginSwitchOutcome Outcome, DateTimeOffset? TitlePassedAt)> RunFlowAsync(
        LoginFlowSession session, TimeSpan timeout, StarRailWindowInfo? expectedWindow, CancellationToken cancellationToken,
        string? expectedIdentity = null)
    {
        var deadline = _now() + timeout;
        var trail = new List<string>();
        DateTimeOffset? titlePassed = null;
        LoginStateObservation? last = null;
        LoginScreenView? lastView = null;
        while (_now() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (observation, view) = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            last = observation;
            lastView = view;
            trail.Add(Describe(observation));
            if (expectedWindow is not null && observation.State == LoginScreenState.Title &&
                observation.Accounts.Count > 0 && expectedIdentity is { } identity &&
                observation.Accounts.Any(a => a.NormalizedPhone != identity))
                return (Fatal(LoginSwitchStage.VerifyingSession, "标题页欢迎账号与已选账号不一致，已停止。") with
                    { Trail = trail }, titlePassed);
            if (expectedWindow is not null && !_locator.IsSameProcessAlive(expectedWindow))
                return (Fatal(LoginSwitchStage.VerifyingSession, "选择账号后游戏进程已变化或退出，会话与所选账号的关联中断。") with
                    { Trail = trail }, titlePassed);

            var action = session.Decide(observation, _now());
            _log?.Invoke($"{LoginFlowSession.Describe(observation.State)} -> {action.Kind}: {action.Reason}");
            switch (action.Kind)
            {
                case LoginActionKind.Done:
                    return (LoginSwitchOutcome.Success(LoginSwitchStage.Done, action.Reason) with { Trail = trail },
                        titlePassed);
                case LoginActionKind.Wait:
                    await _delay(Poll, cancellationToken).ConfigureAwait(false);
                    break;
                case LoginActionKind.Click:
                {
                    var window = view.Window ?? _locator.Find();
                    if (window is null)
                        return (Fatal(LoginSwitchStage.FindingWindow, "准备点击时游戏窗口消失。") with { Trail = trail },
                            titlePassed);
                    if (!await ClickAsync(window, action.Target!.CenterX, action.Target.CenterY, cancellationToken)
                            .ConfigureAwait(false))
                        return (Fatal(LoginSwitchStage.WaitingForLogin, "点击失败：" + action.Reason) with { Trail = trail },
                            titlePassed);
                    if (observation.State == LoginScreenState.Title && session.Goal == LoginGoal.InWorld)
                        titlePassed = _now();
                    await _delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
                    break;
                }
                case LoginActionKind.RestartGame:
                {
                    var stop = await StopGameAsync(cancellationToken).ConfigureAwait(false);
                    if (!stop.Ok) return (stop with { Fatal = true, Trail = trail }, titlePassed);
                    var started = await LaunchGameAsync(cancellationToken).ConfigureAwait(false);
                    if (!started.Ok)
                        return (Fatal(LoginSwitchStage.FindingWindow, started.Error ?? "无法重启星铁。") with { Trail = trail },
                            titlePassed);
                    await _delay(Poll, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case LoginActionKind.NeedUser:
                    await SaveEvidenceAsync(view, "needs-user-" + observation.State).ConfigureAwait(false);
                    return (Fatal(LoginSwitchStage.WaitingForLogin, action.Reason + " 批次已停止，请完成后重新开始。") with
                        { NeedsUser = true, Trail = trail }, titlePassed);
                default:
                    await SaveEvidenceAsync(view, "stopped-" + observation.State).ConfigureAwait(false);
                    return (Fatal(LoginSwitchStage.WaitingForLogin, action.Reason) with { Trail = trail }, titlePassed);
            }
        }
        if (lastView is not null) await SaveEvidenceAsync(lastView, "timeout-" + last?.State).ConfigureAwait(false);
        var lastText = last is null ? "" : $"最后画面：{LoginFlowSession.Describe(last.State)}，{last.Detail}";
        return (Fatal(LoginSwitchStage.WaitingForLogin,
            $"{(session.Goal == LoginGoal.InWorld ? "等待进入游戏" : "等待账号选择界面")}超时（{DurationText.Seconds(timeout)}）。{lastText}") with
            { Trail = trail }, titlePassed);
    }

    /// <summary>
    /// Reads and classifies the current frame. If the frame was rejected because the game is covered or minimised,
    /// the elevated host (which can legitimately foreground an elevated window) is asked once to bring it forward.
    /// </summary>
    private async Task<(LoginStateObservation Observation, LoginScreenView View)> ObserveAsync(CancellationToken cancellationToken)
    {
        var view = await _source.ScanAsync(cancellationToken).ConfigureAwait(false);
        if (view.Result.Failure is LoginScanFailure.WindowOccluded or LoginScanFailure.WindowMinimized &&
            view.Window is { } covered)
        {
            var front = await _input.ForegroundAsync(covered, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"foreground ok={front.Ok} {front.Detail ?? front.Error}");
            await _delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            view = await _source.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        if (view.Window is null)
        {
            var detail = view.Result.Detail;
            var state = _locator.IsProcessRunning() ? LoginScreenState.Loading : LoginScreenState.NoWindow;
            return (new LoginStateObservation(state, [], [], null, detail), view);
        }
        if (view.Frame is null && view.Lines.Count == 0)
            return (new LoginStateObservation(LoginScreenState.Unknown, [], [], null, view.Result.Detail), view);

        var lines = view.Lines.Count > 0 ? view.Lines : [];
        var region = view.Panel is null ? null
            : new ScreenRegion(view.Panel.X - 8, view.Panel.Y - 8, view.Panel.Width + 16, view.Panel.Height + 16);
        return (LoginStateClassifier.Classify(lines, view.Panel is not null, region,
            view.Frame is not null && LoginRetentionDetector.IsSelected(view.Frame, lines)), view);
    }

    /// <summary>Local-only screenshot of the frame that stopped the flow, so the screen can be inspected later.</summary>
    private async Task SaveEvidenceAsync(LoginScreenView view, string name)
    {
        if (_saveEvidence is null || view.Frame is null) return;
        try { await _saveEvidence(view, name).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.Invoke("evidence not saved: " + ex.GetType().Name); }
    }

    private async Task<bool> ClickAsync(StarRailWindowInfo window, double x, double y, CancellationToken cancellationToken)
    {
        var click = await _input.ClickAsync(window, (int)Math.Round(x), (int)Math.Round(y), cancellationToken)
            .ConfigureAwait(false);
        if (!click.Ok) _log?.Invoke("click failed: " + click.Error);
        return click.Ok;
    }

    private static LoginSwitchOutcome Fatal(LoginSwitchStage stage, string detail) =>
        LoginSwitchOutcome.Fail(stage, detail) with { Fatal = true };

    private static string Describe(LoginStateObservation o) =>
        o.Accounts.Count == 0
            ? LoginFlowSession.Describe(o.State)
            : $"{LoginFlowSession.Describe(o.State)}[{string.Join(",", o.Accounts.Select(a => a.MaskedPhone))}]";

    private static AccountSwitchEvidence Judge(AccountSwitchEvidence evidence, IReadOnlyList<string> trail) =>
        AccountSwitchVerifier.Judge(evidence with { Trail = trail.TakeLast(40).ToArray() });
}
