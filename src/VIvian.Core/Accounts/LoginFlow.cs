using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VIvian.Core.Accounts;

/// <summary>
/// What the Star Rail window currently shows, as far as local OCR can tell. Every state is derived from text that
/// is actually visible; anything that does not match a known screen is <see cref="Unknown"/> and is never clicked.
/// </summary>
public enum LoginScreenState
{
    Unknown,
    NoWindow,
    Launcher,
    Loading,
    /// <summary>Title screen after auto-login: "点击进入". Clicking it enters the world as the *previous* account.</summary>
    Title,
    /// <summary>"确定退出登录？" style confirmation after pressing the logout control.</summary>
    LogoutConfirm,
    /// <summary>Collapsed login card: one saved account visible with "进入游戏".</summary>
    LoginCard,
    /// <summary>The saved-account drop-down is open and shows more than one row.</summary>
    AccountList,
    /// <summary>Password, verification code, slider or other security step. Only the user may complete it.</summary>
    AuthChallenge,
    /// <summary>User agreement / privacy consent. Consent belongs to the user, VIvian never presses "同意".</summary>
    AgreementRequired,
    /// <summary>A plain notice with an explicit acknowledge button ("我知道了").</summary>
    Notice,
    InGame,
}

public sealed record LoginStateObservation(
    LoginScreenState State,
    IReadOnlyList<RecognizedText> Lines,
    IReadOnlyList<LoginAccountObservation> Accounts,
    RecognizedText? ActionTarget,
    string Detail)
{
    public static LoginStateObservation Missing(string detail) =>
        new(LoginScreenState.NoWindow, [], [], null, detail);
}

/// <summary>Turns OCR lines into a <see cref="LoginScreenState"/>. Pure function; no clicks, no I/O.</summary>
public static class LoginStateClassifier
{
    // Texts are compared after removing whitespace. Keep lists short and specific: a broad keyword would make an
    // unrelated in-game panel look like a login screen.
    /// <summary>Labels of the title-screen control that signs the current account out (never "退出游戏").</summary>
    public static readonly string[] LogoutControls = ["退出登录", "登出", "切换账号", "注销登录", "退出账号", "更换账号"];
    private static readonly string[] ConfirmButtons = ["确定", "确认"];
    private static readonly string[] AuthWords =
        ["验证码", "安全验证", "拖动滑块", "滑块", "请完成验证", "输入密码", "请输入密码", "短信验证", "人机验证", "实名认证"];
    private static readonly string[] AgreementWords = ["用户协议", "隐私政策", "隐私协议"];
    private static readonly string[] LoadingWords = ["加载中", "正在加载", "资源下载", "正在下载", "健康游戏忠告", "抵制不良游戏"];
    private static readonly string[] LauncherWords = ["米哈游启动器", "启动器设置", "游戏设置"];

    public static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    public static LoginStateObservation Classify(IReadOnlyList<RecognizedText> lines, bool loginPanelDetected = false,
        ScreenRegion? panel = null)
    {
        if (lines.Count == 0)
            return new(LoginScreenState.Unknown, lines, [], null, "画面上没有可识别文字。");
        var compact = lines.Select(l => Compact(l.Text)).ToArray();
        bool Has(string word) => compact.Any(t => t.Contains(word, StringComparison.Ordinal));
        bool HasAny(IEnumerable<string> words) => words.Any(Has);

        var accounts = MaskedPhoneParser.FindAll(lines, panel);

        // Authentication always wins: if any security step is visible nothing may be clicked.
        if (HasAny(AuthWords))
            return new(LoginScreenState.AuthChallenge, lines, accounts, null,
                "出现密码、验证码或安全验证，需要用户本人完成。");

        if (HasAny(AgreementWords) && Has("同意"))
            return new(LoginScreenState.AgreementRequired, lines, accounts, null,
                "出现用户协议/隐私政策确认，需要用户本人同意。");

        var logoutLine = FindAny(lines.Where(l => !Compact(l.Text).Contains("退出游戏", StringComparison.Ordinal)).ToArray(),
            LogoutControls);
        var confirm = FindAny(lines, ConfirmButtons);
        // The confirmation dialog is drawn over the dimmed title screen, so "点击进入" may still be readable behind
        // it; the dialog (confirm + cancel + a logout phrase) takes precedence over the title.
        if (confirm is not null && Has("取消") && (logoutLine is not null || Has("退出当前")))
            return new(LoginScreenState.LogoutConfirm, lines, accounts, confirm, "退出登录确认框。");

        var enterGame = FindAny(lines, ["进入游戏"]);
        if (accounts.Count > 0 && (enterGame is not null || Has("登录其他账号") || loginPanelDetected))
        {
            var state = accounts.Count > 1 ? LoginScreenState.AccountList : LoginScreenState.LoginCard;
            return new(state, lines, accounts, enterGame,
                state == LoginScreenState.AccountList
                    ? $"已保存账号列表已展开（{accounts.Count} 项）。"
                    : $"登录卡片显示账号 {accounts[0].MaskedPhone}。");
        }
        if (accounts.Count == 0 && (Has("手机号") || Has("账号密码登录") || Has("获取验证码")))
            return new(LoginScreenState.AuthChallenge, lines, accounts, null,
                "登录界面没有已保存账号，需要用户本人登录。");

        var title = FindAny(lines, ["点击进入"]);
        if (title is not null)
            return new(LoginScreenState.Title, lines, accounts, logoutLine,
                logoutLine is null ? "标题界面（未识别到退出登录按钮）。" : "标题界面（可退出登录）。");

        var notice = FindAny(lines, ["我知道了"]);
        if (notice is not null) return new(LoginScreenState.Notice, lines, accounts, notice, "提示框。");

        if (Has("开始游戏") && HasAny(LauncherWords))
            return new(LoginScreenState.Launcher, lines, accounts, null, "这是启动器窗口，不是游戏窗口。");

        if (GameUidParser.LooksInGame(lines))
            return new(LoginScreenState.InGame, lines, accounts, null, "游戏世界。");

        if (HasAny(LoadingWords) || compact.Any(t => t.EndsWith('%')))
            return new(LoginScreenState.Loading, lines, accounts, null, "加载中。");

        return new(LoginScreenState.Unknown, lines, accounts, null, "无法识别当前画面。");
    }

    public static RecognizedText? FindAny(IReadOnlyList<RecognizedText> lines, IEnumerable<string> words)
    {
        foreach (var word in words)
        {
            var hit = lines.Where(l => Compact(l.Text).Contains(word, StringComparison.Ordinal))
                // Prefer the tightest box: OCR sometimes returns a long sentence that merely mentions the word.
                .OrderBy(l => Compact(l.Text).Length).ThenByDescending(l => l.Width * l.Height).FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }
}

public enum LoginGoal
{
    /// <summary>Reach the saved-account card/list without entering the world as anyone.</summary>
    AccountSelectable,
    /// <summary>After "进入游戏" for the selected account: pass the title screen and reach the world.</summary>
    InWorld,
}

public enum LoginActionKind { Done, Click, Wait, RestartGame, NeedUser, Fail }

public sealed record LoginAction(LoginActionKind Kind, string Reason, RecognizedText? Target = null)
{
    public static LoginAction Done(string reason) => new(LoginActionKind.Done, reason);
    public static LoginAction Wait(string reason) => new(LoginActionKind.Wait, reason);
    public static LoginAction Click(RecognizedText target, string reason) => new(LoginActionKind.Click, reason, target);
    public static LoginAction Restart(string reason) => new(LoginActionKind.RestartGame, reason);
    public static LoginAction NeedUser(string reason) => new(LoginActionKind.NeedUser, reason);
    public static LoginAction Fail(string reason) => new(LoginActionKind.Fail, reason);
}

public sealed record LoginFlowBudget(int MaxClicksPerState = 3, int MaxUnknownStreak = 8, int MaxSteps = 60,
    int MaxRestarts = 1)
{
    public static LoginFlowBudget Default { get; } = new();
}

/// <summary>
/// One goal-directed walk through the login screens. The session counts clicks per state, consecutive unknown
/// screens, total steps and restarts, so a screen that does not react can never cause an endless click loop.
/// Unknown screens only ever produce <see cref="LoginActionKind.Wait"/>.
/// </summary>
public sealed class LoginFlowSession(LoginGoal goal, LoginFlowBudget? budget = null)
{
    private readonly LoginFlowBudget _budget = budget ?? LoginFlowBudget.Default;
    private readonly Dictionary<LoginScreenState, int> _clicks = [];
    private readonly List<(LoginScreenState State, DateTimeOffset At)> _trail = [];
    private int _unknownStreak;
    private int _steps;
    private int _restarts;

    public LoginGoal Goal => goal;
    public IReadOnlyList<(LoginScreenState State, DateTimeOffset At)> Trail => _trail;
    public int Restarts => _restarts;

    public LoginAction Decide(LoginStateObservation observation, DateTimeOffset? at = null)
    {
        _steps++;
        _trail.Add((observation.State, at ?? DateTimeOffset.UtcNow));
        if (_steps > _budget.MaxSteps)
            return LoginAction.Fail($"登录流程超过 {_budget.MaxSteps} 步仍未完成，已停止以免反复点击。");

        if (observation.State is LoginScreenState.Unknown or LoginScreenState.Loading)
        {
            _unknownStreak++;
            return _unknownStreak > _budget.MaxUnknownStreak
                ? LoginAction.Fail($"连续 {_unknownStreak} 次无法识别画面，已停止；不会在未知画面上盲点。")
                : LoginAction.Wait(observation.Detail);
        }
        _unknownStreak = 0;

        var action = goal == LoginGoal.AccountSelectable ? ForAccountSelection(observation) : ForWorld(observation);
        if (action.Kind == LoginActionKind.Click)
        {
            var count = _clicks.GetValueOrDefault(observation.State) + 1;
            _clicks[observation.State] = count;
            if (count > _budget.MaxClicksPerState)
                return LoginAction.Fail($"在“{Describe(observation.State)}”画面已点击 {count - 1} 次仍未变化，已停止。");
        }
        if (action.Kind == LoginActionKind.RestartGame)
        {
            if (++_restarts > _budget.MaxRestarts)
                return LoginAction.Fail("已重启游戏仍未回到账号选择界面，已停止。");
        }
        return action;
    }

    private static LoginAction ForAccountSelection(LoginStateObservation o) => o.State switch
    {
        LoginScreenState.LoginCard or LoginScreenState.AccountList => LoginAction.Done(o.Detail),
        // Never press "点击进入" here: after auto-login it would enter the world as the previous account.
        LoginScreenState.Title when o.ActionTarget is not null => LoginAction.Click(o.ActionTarget, "退出当前自动登录的账号。"),
        LoginScreenState.Title => LoginAction.Fail("标题界面没有识别到“退出登录”按钮，无法安全回到账号列表；不会点击“点击进入”。"),
        LoginScreenState.LogoutConfirm when o.ActionTarget is not null => LoginAction.Click(o.ActionTarget, "确认退出登录。"),
        LoginScreenState.LogoutConfirm => LoginAction.Fail("退出登录确认框没有可识别的确定按钮。"),
        LoginScreenState.InGame => LoginAction.Restart("游戏仍在世界内，需要重启后从标题界面退出登录。"),
        LoginScreenState.Notice when o.ActionTarget is not null => LoginAction.Click(o.ActionTarget, "关闭提示框。"),
        LoginScreenState.AuthChallenge or LoginScreenState.AgreementRequired => LoginAction.NeedUser(o.Detail),
        LoginScreenState.Launcher => LoginAction.Fail(o.Detail),
        LoginScreenState.NoWindow => LoginAction.Fail(o.Detail),
        _ => LoginAction.Wait(o.Detail),
    };

    private static LoginAction ForWorld(LoginStateObservation o) => o.State switch
    {
        LoginScreenState.InGame => LoginAction.Done(o.Detail),
        LoginScreenState.Title when LoginStateClassifier.FindAny(o.Lines, ["点击进入"]) is { } enter =>
            LoginAction.Click(enter, "已登录所选账号，点击进入。"),
        // The card may stay for a moment after "进入游戏"; waiting is safe, clicking again is not.
        LoginScreenState.LoginCard or LoginScreenState.AccountList => LoginAction.Wait("登录卡片尚未关闭。"),
        LoginScreenState.Notice when o.ActionTarget is not null => LoginAction.Click(o.ActionTarget, "关闭提示框。"),
        LoginScreenState.AuthChallenge or LoginScreenState.AgreementRequired => LoginAction.NeedUser(o.Detail),
        LoginScreenState.LogoutConfirm => LoginAction.Fail("进入游戏途中出现退出登录确认框，状态不明确，已停止。"),
        LoginScreenState.NoWindow => LoginAction.Fail(o.Detail),
        LoginScreenState.Launcher => LoginAction.Fail(o.Detail),
        _ => LoginAction.Wait(o.Detail),
    };

    public static string Describe(LoginScreenState state) => state switch
    {
        LoginScreenState.Unknown => "未知",
        LoginScreenState.NoWindow => "无游戏窗口",
        LoginScreenState.Launcher => "启动器",
        LoginScreenState.Loading => "加载中",
        LoginScreenState.Title => "标题界面",
        LoginScreenState.LogoutConfirm => "退出登录确认",
        LoginScreenState.LoginCard => "登录卡片",
        LoginScreenState.AccountList => "账号列表",
        LoginScreenState.AuthChallenge => "认证挑战",
        LoginScreenState.AgreementRequired => "协议确认",
        LoginScreenState.Notice => "提示框",
        LoginScreenState.InGame => "游戏世界",
        _ => state.ToString(),
    };
}

/// <summary>
/// Masked, shareable evidence that a batch really logged in as the intended account. Only masked phone numbers
/// and an optional in-game UID are kept; no screenshot, token or full phone number.
/// </summary>
public sealed record AccountSwitchEvidence
{
    public string TargetMasked { get; init; } = "";
    public string? PreviousMasked { get; init; }
    public string? ConfirmedSelectionMasked { get; init; }
    public DateTimeOffset? SelectionConfirmedAt { get; init; }
    public DateTimeOffset? EnterPressedAt { get; init; }
    public DateTimeOffset? TitlePassedAt { get; init; }
    public DateTimeOffset? InGameAt { get; init; }
    public string? Uid { get; init; }
    public string? PreviousUid { get; init; }
    public string? ExpectedUid { get; init; }
    public bool RestartAfterSelection { get; init; }
    public IReadOnlyList<string> Trail { get; init; } = [];
    public string Verdict { get; init; } = "unverified";
    public string VerdictDetail { get; init; } = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialises with UIDs masked; raw UIDs are only ever compared in memory.</summary>
    public string ToJson() => JsonSerializer.Serialize(this with
    {
        Uid = MaskOrNull(Uid), PreviousUid = MaskOrNull(PreviousUid), ExpectedUid = MaskOrNull(ExpectedUid),
    }, Json);

    private static string? MaskOrNull(string? uid) => uid is { Length: > 0 } ? AccountSwitchVerifier.Mask(uid) : null;
}

public static class AccountSwitchVerifier
{
    /// <summary>
    /// "In game" alone proves nothing about *which* account. A switch counts as verified only when the collapsed
    /// login card showed exactly the target before "进入游戏", the session continued without a restart until the
    /// world loaded, and any readable UID is consistent with the profile and different from the previous account.
    /// </summary>
    public static AccountSwitchEvidence Judge(AccountSwitchEvidence e)
    {
        string? problem = null;
        var target = MaskedPhoneParser.Normalize(e.TargetMasked);
        if (e.ConfirmedSelectionMasked is null || MaskedPhoneParser.Normalize(e.ConfirmedSelectionMasked) != target)
            problem = "进入游戏前没有确认登录卡片显示的是目标账号。";
        else if (e.EnterPressedAt is null || e.SelectionConfirmedAt is null || e.EnterPressedAt < e.SelectionConfirmedAt)
            problem = "缺少“确认目标账号 → 进入游戏”的顺序证据。";
        else if (e.InGameAt is null || e.InGameAt < e.EnterPressedAt)
            problem = "没有在进入游戏之后观察到游戏世界。";
        else if (e.RestartAfterSelection)
            problem = "选择账号后游戏发生过重启，会话与所选账号的关联中断。";
        else if (e.ExpectedUid is { Length: > 0 } expected && e.Uid is { Length: > 0 } actual &&
                 !string.Equals(expected, actual, StringComparison.Ordinal))
            problem = $"游戏内 UID {Mask(actual)} 与档案记录 {Mask(expected)} 不一致。";
        else if (e.PreviousUid is { Length: > 0 } previous && e.Uid is { Length: > 0 } current &&
                 string.Equals(previous, current, StringComparison.Ordinal))
            problem = $"游戏内 UID {Mask(current)} 与上一个账号相同，切换可能没有生效。";

        if (problem is not null) return e with { Verdict = "mismatch", VerdictDetail = problem };
        var strength = e.Uid is { Length: > 0 }
            ? $"已确认目标账号并进入游戏，UID {Mask(e.Uid)}。"
            : "已确认目标账号并进入游戏（未读到 UID，依据为登录卡片确认与连续会话）。";
        return e with { Verdict = "verified", VerdictDetail = strength };
    }

    /// <summary>UIDs are not secrets but are still masked in shared evidence.</summary>
    public static string Mask(string uid) =>
        uid.Length <= 4 ? new string('*', uid.Length) : uid[..2] + new string('*', uid.Length - 4) + uid[^2..];
}

/// <summary>Why a captured frame can or cannot be trusted to show the game rather than VIvian or another window.</summary>
public sealed record CaptureCheck(
    bool WindowFound,
    bool ProcessIsGame,
    bool Minimized,
    string Method,
    int Samples,
    int SamplesOwnedByGame,
    bool FrameBlank,
    string? OccluderDescription = null);

public static class CaptureVerdict
{
    public static (bool Accept, LoginScanFailure Failure, string Detail) Evaluate(CaptureCheck c)
    {
        if (!c.WindowFound) return (false, LoginScanFailure.WindowNotFound, "没有找到星铁游戏窗口。请先启动游戏并停在登录界面。");
        if (!c.ProcessIsGame) return (false, LoginScanFailure.CapturedWrongWindow, "找到的窗口不属于 StarRail.exe，已拒绝使用该画面。");
        if (c.Minimized) return (false, LoginScanFailure.WindowMinimized,
            "游戏窗口处于最小化状态，VIvian（普通权限）无法恢复以管理员运行的游戏窗口。请在任务栏点开游戏后再扫描。");
        if (c.FrameBlank) return (false, LoginScanFailure.CaptureFailed, "截取到的游戏画面为全黑，可能游戏仍在加载或处于独占全屏。请切换到窗口/无边框模式后重试。");
        if (c.Method == "print") return (true, LoginScanFailure.None, "通过窗口渲染截取（不受遮挡影响）。");
        if (c.Samples <= 0) return (false, LoginScanFailure.CaptureFailed, "无法校验截图区域的窗口归属。");
        if (c.SamplesOwnedByGame < c.Samples)
        {
            var who = string.IsNullOrWhiteSpace(c.OccluderDescription) ? "其他窗口" : c.OccluderDescription;
            return (false, LoginScanFailure.WindowOccluded,
                $"游戏窗口被{who}遮挡（{c.Samples - c.SamplesOwnedByGame}/{c.Samples} 个校验点不属于游戏），已拒绝该截图以免把其他画面当成游戏。请把游戏窗口切到前台后重试。");
        }
        return (true, LoginScanFailure.None, $"屏幕截取，{c.Samples} 个校验点均属于游戏窗口。");
    }
}

/// <summary>Masked, local-only description of one scan, suitable for logs and the scan-session table.</summary>
public sealed record ScanEvidence(
    long WindowHandle,
    int ProcessId,
    string ProcessName,
    int ClientWidth,
    int ClientHeight,
    DateTimeOffset CapturedAt,
    CaptureCheck Check,
    IReadOnlyList<LoginAccountObservation> Accounts)
{
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"窗口 0x{WindowHandle:X} / {ProcessName} pid {ProcessId} / 客户区 {ClientWidth}x{ClientHeight} / ");
        builder.Append(CultureInfo.InvariantCulture,
            $"截图 {CapturedAt:yyyy-MM-dd HH:mm:ss}Z / 方式 {Check.Method} / 校验点 {Check.SamplesOwnedByGame}/{Check.Samples}");
        foreach (var a in Accounts)
            builder.Append(CultureInfo.InvariantCulture,
                $" / [{a.RowOrder + 1}] {a.MaskedPhone} @({a.CenterX:0},{a.CenterY:0}) 置信度 {a.Confidence:0.00}");
        return builder.ToString();
    }
}
