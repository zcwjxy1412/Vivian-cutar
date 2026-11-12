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
        while (timeout.Elapsed < policy.LoginScreen)
        {
            ct.ThrowIfCancellationRequested();
            if (_game is null || _game.HasExited || EngineBusy()) return Failure("本轮窗口已退出或 OneDragon 已经启动。");
            var window = ReadWindow(activate: true);
            if (window is not null)
            {
                var (pixels, check) = ScreenCapture.CaptureChecked(window, GameProcess);
                if (pixels is not null && CaptureVerdict.Evaluate(check).Accept)
                {
                    var ocr = await WindowsOcrReader.ReadAsync(pixels, ct).ConfigureAwait(false);
                    if (!ocr.Available) return Failure("无法使用本地中文 OCR 核对已保存账号。");
                    var step = await ClassifyAsync(pixels, ocr.Lines, target.NormalizedPhone, ct).ConfigureAwait(false);
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
                            return Failure("绝区零需要重新登录、验证或确认协议。") with { NeedsUser = true };
                        if (step.Point is { } point)
                        {
                            var count = counts.GetValueOrDefault(step.Action) + 1;
                            counts[step.Action] = count;
                            if (count > 2 || !Click(window, point))
                                return Failure("登录画面变化或账号选择未响应。");
                            await Task.Delay(1100, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                else stable = 0;
            }
            else stable = 0;
            await Task.Delay(450, ct).ConfigureAwait(false);
        }
        return Failure("未能核对唯一目标账号。请检查绝区零登录画面和该档案的脱敏手机号。");
    }

    private async Task<(string Action, ScreenRegion? Point)> ClassifyAsync(CapturedFrame frame, IReadOnlyList<RecognizedText> lines,
        string target, CancellationToken ct)
    {
        ScreenRegion Box(double x, double y, double width, double height) =>
            ZenlessAccountImport.ScaleBox(frame.Width, frame.Height, x, y, width, height);
        RecognizedText? Text(string text, ScreenRegion box) =>
            LoginStateClassifier.FindExact(lines.Where(line => box.Contains(line.CenterX, line.CenterY)).ToArray(), text);
        ScreenRegion? Match(string name, ScreenRegion box) => GenshinLoginTemplates.Match(frame, _templates[name], box, ct);

        var panel = Box(540, 250, 820, 570);
        var modal = lines.Where(line => panel.Contains(line.CenterX, line.CenterY)).ToArray();
        var retain = Text("退出并保留登录记录", Box(610, 478, 390, 95));
        if (retain is not null)
        {
            if (!LoginRetentionDetector.IsSelected(frame, modal))
                return ("retain", new(retain.X - 1.4 * retain.Height, retain.Y, retain.Height, retain.Height));
            var button = Text("确定", Box(1000, 680, 260, 140)) ?? Text("退出", Box(1000, 680, 260, 140));
            return ("logout", Region(button));
        }

        if (modal.Any(line => new[] { "安全验证", "请输入密码", "请输入验证码", "短信验证码", "重新登录", "同意协议" }
            .Any(marker => LoginStateClassifier.Compact(line.Text).Contains(marker, StringComparison.Ordinal))))
            return ("needs_user", null);

        var dropdown = Box(1197, 470, 65, 65);
        var expanded = Match("account_dropdown_collapse", dropdown);
        var closed = Match("account_dropdown_expand", dropdown);
        var confirm = Text("进入游戏", Box(880, 580, 170, 85));
        if (expanded is not null && closed is null)
        {
            var rows = await ZenlessAccountImport.ReadExpandedRowsAsync(frame, lines, ct).ConfigureAwait(false);
            var matches = rows.Where(row => row.NormalizedPhone == target).ToArray();
            return matches.Length == 1
                ? ("choose", new(matches[0].X, matches[0].Y, matches[0].Width, matches[0].Height)) : ("wait", null);
        }
        if (closed is not null && expanded is null && confirm is not null)
        {
            var header = MaskedPhoneParser.FindAll(lines, Box(610, 450, 590, 82));
            return header.Count == 1 && header[0].NormalizedPhone == target
                ? ("handoff", null) : ("dropdown", closed);
        }
        var title = Text("点击进入游戏", Box(770, 827, 400, 224));
        if (title is not null && Text("退出登录", panel) is null)
            return ("switch", Match("switch_account", Box(1776, 950, 100, 94)));
        return ("wait", null);
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

    private static ScreenRegion? Region(RecognizedText? text) =>
        text is null ? null : new(text.X, text.Y, text.Width, text.Height);

    private static LoginSwitchOutcome Failure(string message) =>
        LoginSwitchOutcome.Fail(LoginSwitchStage.WaitingForLogin, message) with { Fatal = true };
}
