using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.OneDragon;

internal sealed class ZenlessLoginDriver(string gamePath, string root, Action beforeLaunch) : ILoginSwitchDriver, IDisposable
{
    private const string GameProcess = "ZenlessZoneZero";
    private readonly Dictionary<string, CapturedFrame> _templates = [];
    private Process? _game;
    private bool _handedOff;
    private bool _keepGameForUser;
    internal bool LeavesGameForUser => _keepGameForUser && _game is { HasExited: false };
    private DateTimeOffset? _started;
    internal int GamePid => _handedOff && _game is { HasExited: false } game
        ? game.Id : throw new InvalidOperationException("已核对的绝区零登录窗口已经退出。");

    internal static bool IsRunning() => HasProcess(GameProcess);

    private static bool HasProcess(string name) => StarRailWindow.IsProcessRunning(name);

    internal static bool EngineBusy() => HasProcess("OneDragon-RuntimeLauncher") || HasProcess("OneDragon-Launcher");

    public async Task<LoginSwitchOutcome> EnsureLoginAsync(SwitchPolicy policy, CancellationToken ct)
    {
        if (LaunchBlock() is { } initialBlock) return Failure(initialBlock);
        await LoadTemplatesAsync(ct).ConfigureAwait(false);
        try
        {
            using var image = GenshinGameExecutable.OpenForSignatureVerification(gamePath);
            GenshinGameExecutable.Verify(gamePath, image);
            ct.ThrowIfCancellationRequested();
            if (LaunchBlock() is { } block) return Failure(block);
            beforeLaunch();
            // The batch already runs elevated. Retain the direct launch handle for identification and cleanup.
            _game = WindowsProcessSupervisor.StartInheritedProcess(gamePath, "", Path.GetDirectoryName(gamePath)!);
        }
        catch (Win32Exception ex)
        {
            return Failure($"绝区零客户端启动失败（Windows 错误 {ex.NativeErrorCode}）。请核对客户端路径和管理员权限。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure("无法核对绝区零客户端的米哈游签名、实际路径或桌面交接状态。");
        }
        if (VerifyStartedGame(_game, gamePath) is not { } started)
            return Failure("无法核对本轮直接启动的绝区零进程，已停止交接。");
        _started = started;
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < policy.GameStart)
        {
            ct.ThrowIfCancellationRequested();
            if (_game.HasExited) return Failure("绝区零客户端启动后退出。");
            if (ReadWindow(activate: true) is not null)
                return LoginSwitchOutcome.Success(LoginSwitchStage.WaitingForLogin, "已找到本轮绝区零窗口。");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return Failure("等待绝区零窗口超时。");
    }

    private static string? LaunchBlock()
    {
        if (IsRunning()) return "检测到已打开的绝区零。请退出游戏，然后让 Vivian 从首页启动并接管本轮。";
        if (EngineBusy()) return "检测到 OneDragon 正在运行。请退出 OneDragon 窗口后重试。";
        return StarRailWindow.IsProcessRunning() || HasProcess("YuanShen")
            ? "原神或崩铁正在使用桌面。请退出该游戏后重试。" : null;
    }

    internal static DateTimeOffset? VerifyStartedGame(Process game, string expectedPath)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            if (game.HasExited || game.SessionId != current.SessionId) return null;
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(game.SafeHandle, 0, path, ref length)
                && string.Equals(path.ToString(), expectedPath, StringComparison.OrdinalIgnoreCase)
                ? StarRailWindow.StartTime(game) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);

    public async Task<LoginSwitchOutcome> SwitchAsync(LoginTarget target, SwitchPolicy policy, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stable = 0;
        StarRailWindowInfo? previous = null;
        var waiting = "尚未找到登录账号确认页。";
        string? lastAction = null;
        var awaitingLogout = false;
        while (timeout.Elapsed < policy.LoginScreen)
        {
            ct.ThrowIfCancellationRequested();
            if (_game is null || _game.HasExited) return Failure("本轮绝区零客户端已退出，尚未交给 OneDragon。");
            if (EngineBusy()) return Failure("检测到另一个 OneDragon 已打开，已停止登录操作。");
            var window = ReadWindow();
            if (window is not null)
            {
                var (pixels, check) = ScreenCapture.CaptureChecked(window, GameProcess);
                if (pixels is not null && CaptureVerdict.Evaluate(check).Accept)
                {
                    var ocr = await WindowsOcrReader.ReadAsync(pixels, ct).ConfigureAwait(false);
                    if (!ocr.Available) return Failure("无法使用本地中文 OCR 核对已保存账号。");
                    var step = await ClassifyAsync(pixels, ocr.Lines, target.NormalizedPhone, ct, awaitingLogout).ConfigureAwait(false);
                    lastAction = step.Action;
                    if (step.Action is "choose" or "handoff" or "dropdown" or "header_unknown" or "button_unknown" or "account_missing")
                        awaitingLogout = false;
                    waiting = step.Action switch
                    {
                        "header_unknown" => "已找到登录卡片，但未能唯一核对脱敏手机号。",
                        "button_unknown" => "已读取登录账号，但未能确认「进入游戏」按钮。",
                        "account_missing" => "已打开保存账号列表，但未找到唯一的目标账号。",
                        "logout_pending" => "正在核对退出登录弹窗和保留登录记录选项。",
                        _ => "尚未完成登录账号确认。",
                    };
                    if (step.Action == "handoff" && ReadWindow() == window)
                    {
                        stable = previous == window ? stable + 1 : 1;
                        previous = window;
                        if (stable >= 2)
                        {
                            _handedOff = true;
                            return LoginSwitchOutcome.Success(LoginSwitchStage.ReadyAtGate,
                                "已连续核对目标账号的登录确认卡片。");
                        }
                    }
                    else
                    {
                        stable = 0;
                        if (step.Action == "needs_user")
                            return NeedsUser("绝区零显示了需要本人处理的登录或验证提示。");
                        if (step.Point is { } point)
                        {
                            var count = counts.GetValueOrDefault(step.Action) + 1;
                            counts[step.Action] = count;
                            if (count > 2 || !Click(window, point))
                                return step.Action is "retain" or "logout"
                                    ? NeedsUser("请在绝区零选中「退出并保留登录记录」后确认。")
                                    : Failure("登录画面变化或账号选择未响应。");
                            if (step.Action is "switch" or "logout")
                                awaitingLogout = step.Action == "switch";
                            await Task.Delay(1100, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                else { stable = 0; waiting = "暂时无法读取未被遮挡的绝区零游戏画面。"; }
            }
            else { stable = 0; waiting = "请将绝区零保持在前台。"; }
            await Task.Delay(450, ct).ConfigureAwait(false);
        }
        return awaitingLogout || lastAction == "logout_pending"
            ? NeedsUser("无法核对保留登录记录的选项，请在绝区零选中「退出并保留登录记录」后确认。")
            : Failure($"等待登录账号确认超时（{policy.LoginScreen.TotalSeconds:0} 秒）。" + waiting);
    }

    private async Task<(string Action, ScreenRegion? Point)> ClassifyAsync(CapturedFrame frame, IReadOnlyList<RecognizedText> lines,
        string target, CancellationToken ct, bool awaitingLogout)
    {
        ScreenRegion Box(double x, double y, double width, double height) =>
            ZenlessAccountImport.ScaleBox(frame.Width, frame.Height, x, y, width, height);
        RecognizedText? Text(string text, ScreenRegion box) =>
            LoginStateClassifier.FindExact(lines.Where(line => box.Contains(line.CenterX, line.CenterY)).ToArray(), text);
        ScreenRegion? Match(string name, ScreenRegion box) => GenshinLoginTemplates.Match(frame, _templates[name], box, ct);

        var panel = Box(540, 250, 820, 570);
        var modal = lines.Where(line => panel.Contains(line.CenterX, line.CenterY)).ToArray();
        if (ClassifyLogout(frame, modal) is { } logout)
            return logout;
        if (NeedsAuthentication(modal))
            return ("needs_user", null);

        var dropdown = Box(1197, 470, 65, 65);
        var expanded = Match("account_dropdown_collapse", dropdown);
        var closed = Match("account_dropdown_expand", dropdown);
        if (expanded is not null && closed is null)
        {
            var rows = await ZenlessAccountImport.ReadExpandedRowsAsync(frame, lines, ct).ConfigureAwait(false);
            var matches = rows.Where(row => row.NormalizedPhone == target).ToArray();
            return matches.Length == 1
                ? ("choose", new(matches[0].X, matches[0].Y, matches[0].Width, matches[0].Height)) : ("account_missing", null);
        }
        if (closed is not null && expanded is null)
        {
            var header = await ZenlessAccountImport.ReadHeaderAsync(frame, lines, ct).ConfigureAwait(false);
            if (header is not [{ } selected]) return ("header_unknown", null);
            if (!await ZenlessAccountImport.ConfirmButtonAsync(frame, lines, ct).ConfigureAwait(false))
                return ("button_unknown", null);
            return selected.NormalizedPhone == target ? ("handoff", null) : ("dropdown", closed);
        }
        if (awaitingLogout) return ("logout_pending", null);
        if (closed is not null || expanded is not null) return ("wait", null);
        var title = Text("点击进入游戏", Box(770, 827, 400, 224));
        if (title is not null && Text("退出登录", panel) is null)
            return ("switch", Match("switch_account", Box(1776, 950, 100, 94)));
        return ("wait", null);
    }

    internal static bool NeedsAuthentication(IReadOnlyList<RecognizedText> lines) => lines.Any(line =>
    {
        var value = LoginStateClassifier.Compact(line.Text);
        // The normal clear-record description says the next login will require authentication.
        return value is "重新登录" or "请重新登录" or "登录已过期"
            || new[] { "安全验证", "请输入密码", "请输入验证码", "短信验证码", "同意协议" }
                .Any(marker => value.Contains(marker, StringComparison.Ordinal));
    });

    internal static (string Action, ScreenRegion? Point)? ClassifyLogout(
        CapturedFrame frame, IReadOnlyList<RecognizedText> lines)
    {
        // The CN logout dialog uses two identical lime outlines. The tick inside the ring is the selection.
        // Check both radio controls and the red/green buttons in the 1920x1080 reference layout.
        static bool Lime(int r, int g, int b) => r >= 140 && g >= 175 && g >= r - 8 && b <= 110 && r - b >= 95;
        var clear = SampleIcon(frame, 632, 445.5, 7, 10, Lime);
        var keep = SampleIcon(frame, 632, 525.5, 7, 10, Lime);
        if (clear.Ring < .33 || keep.Ring < .33)
        {
            var knownDialog = LoginStateClassifier.FindExact(lines, "退出登录") is not null
                || LoginStateClassifier.FindExact(lines, "退出并保留登录记录") is not null;
            return knownDialog ? ("logout_pending", null) : null;
        }

        var cancel = SampleIcon(frame, 678.5, 774.5, 8, 8, static (r, g, b) => r >= 140 && r - g >= 95 && r - b >= 95);
        var confirm = SampleIcon(frame, 1001.5, 774.5, 8, 8, static (r, g, b) => g >= 130 && g - r >= 90 && g - b >= 90);
        if (cancel.Ring < .67 || confirm.Ring < .67 || cancel.Center is < .15 or > .75 || confirm.Center is < .15 or > .75)
            return ("logout_pending", null);

        ScreenRegion Box(double x, double y, double width, double height) =>
            ZenlessAccountImport.ScaleBox(frame.Width, frame.Height, x, y, width, height);
        if (keep.Center >= .23 && clear.Center <= .08)
            return ("logout", Box(1100, 752, 88, 46));
        if (clear.Center >= .23 && keep.Center <= .08)
            return ("retain", Box(618, 512, 28, 27));
        return ("logout_pending", null);
    }

    private static (double Center, double Ring) SampleIcon(CapturedFrame frame, double centerX, double centerY,
        int centerRadius, int ringRadius, Func<int, int, int, bool> matchesColor)
    {
        const int outerRadius = 15;
        var box = ZenlessAccountImport.ScaleBox(frame.Width, frame.Height,
            centerX - outerRadius, centerY - outerRadius, 2 * outerRadius, 2 * outerRadius);
        var center = 0;
        var ring = 0;
        var centerTotal = 0;
        var ringTotal = 0;
        for (var y = (int)box.Y; y < box.Bottom; y++)
        for (var x = (int)box.X; x < box.Right; x++)
        {
            var dx = (x + .5) * 1920 / frame.Width - centerX;
            var dy = (y + .5) * 1080 / frame.Height - centerY;
            var distance = dx * dx + dy * dy;
            var i = (y * frame.Width + x) * 4;
            if (distance < centerRadius * centerRadius)
            {
                centerTotal++;
                if (matchesColor(frame.Bgra[i + 2], frame.Bgra[i + 1], frame.Bgra[i])) center++;
            }
            else if (distance >= ringRadius * ringRadius && distance < outerRadius * outerRadius)
            {
                ringTotal++;
                if (matchesColor(frame.Bgra[i + 2], frame.Bgra[i + 1], frame.Bgra[i])) ring++;
            }
        }
        return (center / (double)centerTotal, ring / (double)ringTotal);
    }

    private LoginSwitchOutcome NeedsUser(string reason)
    {
        _keepGameForUser = true;
        return Failure(reason + " 已保留游戏窗口。处理后请退出绝区零，在 Vivian 首页核对桌面交接再重试。")
            with { NeedsUser = true };
    }

    private StarRailWindowInfo? ReadWindow(bool activate = false)
    {
        if (_game is not { HasExited: false } game || StarRailWindow.StartTime(game) != _started || EngineBusy()) return null;
        game.Refresh();
        var hwnd = game.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return null;
        WindowsNative.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != game.Id) return null;
        if (WindowsNative.GetForegroundWindow() != hwnd && (!activate || !StarRailWindow.BringToFront(hwnd))) return null;
        if (!WindowsNative.GetClientRect(hwnd, out var bounds) || bounds.Height < 480
            || Math.Abs((double)bounds.Width / bounds.Height - 16d / 9) > .06) return null;
        var origin = new WindowsNative.Point(0, 0);
        if (!WindowsNative.ClientToScreen(hwnd, ref origin)) return null;
        return new(hwnd, game.Id, "", bounds.Width, bounds.Height, origin.X, origin.Y, _started);
    }

    private bool Click(StarRailWindowInfo window, ScreenRegion region)
    {
        if (_handedOff || ReadWindow() != window || region.X < 0 || region.Y < 0 || region.Right > window.ClientWidth
            || region.Bottom > window.ClientHeight) return false;
        var point = new WindowsNative.Point(window.OriginX + (int)(region.X + region.Width / 2),
            window.OriginY + (int)(region.Y + region.Height / 2));
        bool OwnsPoint() => WindowsNative.GetAncestor(WindowsNative.WindowFromPoint(point), WindowsNative.GaRoot) == window.Handle;
        if (!OwnsPoint() || !WindowsNative.SetCursorPos(point.X, point.Y) || !OwnsPoint() || ReadWindow() != window) return false;
        WindowsNative.mouse_event(WindowsNative.MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        WindowsNative.mouse_event(WindowsNative.MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        return true;
    }

    public async Task<LoginSwitchOutcome> RestartToLoginAsync(SwitchPolicy policy, CancellationToken ct)
    {
        var stopped = await StopGameAsync(ct).ConfigureAwait(false);
        return stopped.Ok ? await EnsureLoginAsync(policy, ct).ConfigureAwait(false) : stopped;
    }

    public Task<LoginSwitchOutcome> StopGameAsync(CancellationToken ct)
    {
        if (_game is null)
            return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "本轮未启动绝区零客户端。"));
        if (EngineBusy())
            return Task.FromResult(Failure("OneDragon 仍在运行，已保留桌面交接标记。"));
        if (!WindowsProcessSupervisor.TerminateOwnedProcess(_game))
            return Task.FromResult(Failure("未能确认本轮绝区零客户端退出。"));
        if (IsRunning()) return Task.FromResult(Failure("仍有绝区零客户端运行，请核对桌面。"));
        _game?.Dispose();
        _game = null;
        _started = null;
        _handedOff = false;
        return Task.FromResult(LoginSwitchOutcome.Success(LoginSwitchStage.Done, "已确认本轮绝区零客户端退出。"));
    }

    public void Dispose() => _game?.Dispose();

    private async Task LoadTemplatesAsync(CancellationToken ct)
    {
        foreach (var name in new[] { "switch_account", "account_dropdown_expand", "account_dropdown_collapse" })
        {
            if (!_templates.ContainsKey(name))
                _templates[name] = await LoadTemplateAsync(root, name, ct).ConfigureAwait(false);
        }
    }

    internal static async Task<CapturedFrame> LoadTemplateAsync(string root, string name, CancellationToken ct)
    {
        var digest = name switch
        {
            "switch_account" => "3C4F74D1368DF72D7DA72122EE47BC06FB6D8BEA7CBA226E37BA5392DF6D276C",
            "account_dropdown_expand" => "C632794B1A632A7339FE879AD191B695AD5992FB55F89695ED7E0D75CDA59444",
            "account_dropdown_collapse" => "B51124493490EBA233C6FF6966E27FF06590F1DE88C79ABEC2B57BB9DFBEC11C",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var file = Path.Combine(root, "assets", "template", "enter_game", name, "raw.png");
        if (ReadOnlyFileAccess.FindReparsePoint(file) is not null)
            throw new InvalidOperationException("OneDragon 登录识别素材包含链接。");
        await using var stream = ReadOnlyFileAccess.OpenForStableRead(file);
        if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, file) || stream.Length > 128 * 1024 ||
            Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != digest)
            throw new InvalidOperationException("OneDragon 登录识别素材与 v2.6.0 不一致。");
        return await WindowsOcrReader.DecodeFileAsync(file, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("无法读取 OneDragon 登录识别素材。");
    }

    private static LoginSwitchOutcome Failure(string message) =>
        LoginSwitchOutcome.Fail(LoginSwitchStage.WaitingForLogin, message) with { Fatal = true };
}
