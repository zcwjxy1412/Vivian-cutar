using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal enum GenshinLaunchWindowState
{
    Missing, Minimized, IncompatibleViewport, Background,
}

internal sealed class GenshinWindowsLoginDesktop(string gamePath, string betterGiProgram, GenshinLoginTemplates templates,
    bool stopOwnedClient)
    : IGenshinLoginDesktop
{
    private const string ClientProcessName = "YuanShen";
    private readonly BetterGiProcessProbe _instances = new();
    private readonly object _clientGate = new();
    private GenshinDesktopFrame? _current;
    private CapturedFrame? _pixels;
    private long _readAt;
    private volatile bool _active = true;
    private (int Pid, DateTimeOffset Started)? _client;
    private Process? _ownedClient;
    private bool _launchUnverified;
    private bool _closed;

    public async Task PrepareAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var windowState = GenshinLaunchWindowState.Missing;
        try
        {
            EnsureDesktopAvailable();
            using var file = GenshinGameExecutable.OpenForSignatureVerification(gamePath);
            await Task.Run(() => GenshinGameExecutable.Verify(gamePath, file), limit.Token).ConfigureAwait(false);
            (int Pid, DateTimeOffset Started) client;
            lock (_clientGate)
            {
                limit.Token.ThrowIfCancellationRequested();
                if (!_active) throw new InvalidOperationException("原神桌面会话已结束。");
                EnsureDesktopAvailable();
                var existing = FindExistingClient();
                if (stopOwnedClient && existing is not null && _ownedClient is null)
                    throw new InvalidOperationException("检测到先前打开的原神。只有本次批次自行启动的客户端才可运行日常。");
                if (stopOwnedClient && (_launchUnverified || _client is not null && _client != existing))
                    throw new InvalidOperationException("原神启动进程已退出或发生变化，已停止登录。");
                _client = client = existing ?? StartClient();
            }

            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                EnsureDesktopAvailable();
                if (!_active || client != FindExistingClient())
                    throw new InvalidOperationException("原神启动进程已退出或发生变化，已停止登录。");
                if (FindForeground() is not null) return;
                using var game = Process.GetProcessById(client.Pid);
                var hwnd = game.MainWindowHandle;
                var owned = IsVerifiedClientWindow(hwnd, client);
                var minimized = owned && WindowsNative.IsIconic(hwnd);
                windowState = ClassifyLaunchWindow(owned, minimized, owned && !minimized && TryFindWindow(hwnd) is not null);
                if (windowState is GenshinLaunchWindowState.Minimized or GenshinLaunchWindowState.Background)
                    StarRailWindow.BringToFront(hwnd);
                await Task.Delay(450, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var detail = windowState switch
            {
                GenshinLaunchWindowState.Minimized => "本轮原神窗口仍处于最小化。",
                GenshinLaunchWindowState.IncompatibleViewport => "本轮原神窗口的可用画面不符合 16∶9 要求。",
                GenshinLaunchWindowState.Background => "本轮原神窗口未取得前台焦点。",
                _ => "未找到归属本轮原神客户端的窗口。",
            };
            throw new InvalidOperationException("等待原神前台窗口超时，已停止登录。" + detail);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Windows 未确认原神启动授权。", ex);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("原神客户端无法启动或核对。" + ex.Message, ex);
        }
    }

    internal static GenshinLaunchWindowState ClassifyLaunchWindow(bool owned, bool minimized, bool usableViewport) =>
        !owned ? GenshinLaunchWindowState.Missing :
        minimized ? GenshinLaunchWindowState.Minimized :
        usableViewport ? GenshinLaunchWindowState.Background : GenshinLaunchWindowState.IncompatibleViewport;

    private void EnsureDesktopAvailable()
    {
        if (_instances.IsBusy(betterGiProgram) || StarRailWindow.IsProcessRunning())
            throw new InvalidOperationException("BetterGI 或崩铁正在使用桌面，原神登录已停止。");
    }

    private (int Pid, DateTimeOffset Started)? FindExistingClient()
    {
        var clients = Process.GetProcessesByName(ClientProcessName);
        try
        {
            return clients.Length switch
            {
                0 => null,
                1 => VerifyClient(clients[0]),
                _ => throw new InvalidOperationException("有多个原神国服客户端，无法安全选择。"),
            };
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    private (int Pid, DateTimeOffset Started) VerifyClient(Process game, SafeProcessHandle? launchHandle = null)
    {
        using var current = Process.GetCurrentProcess();
        if (game.SessionId != current.SessionId || StarRailWindow.StartTime(game) is not { } start ||
            !(launchHandle is null ? MatchesExecutable(game.Id) : MatchesExecutable(launchHandle)))
            throw new InvalidOperationException("原神进程的路径、会话或启动时间无法核对。");
        return (game.Id, start);
    }

    private (int Pid, DateTimeOffset Started) StartClient()
    {
        _launchUnverified = stopOwnedClient;
        Process? started = null;
        try
        {
            started = WindowsProcessSupervisor.StartUserProcess(gamePath, Path.GetDirectoryName(gamePath)!)
                ?? throw new InvalidOperationException("Windows 没有返回本次启动的原神进程。");
            // Check the retained launch handle while the verified executable is still locked for reading.
            var client = VerifyClient(started, stopOwnedClient ? started.SafeHandle : null);
            if (stopOwnedClient) _ownedClient = started;
            AllowSetForegroundWindow((uint)client.Pid);
            _launchUnverified = false;
            return client;
        }
        catch (Win32Exception ex) when (started is null && ex.NativeErrorCode == 1223)
        {
            _launchUnverified = false;
            throw;
        }
        finally
        {
            if (!ReferenceEquals(_ownedClient, started)) started?.Dispose();
        }
    }

    public LoginSwitchOutcome StopOwnedClient()
    {
        lock (_clientGate)
        {
            if (_closed || !stopOwnedClient) return CleanupFailed("原神桌面会话无法确认客户端退出。");
            DisableInput();
            if (_launchUnverified)
                return CleanupFailed("未能核对本次启动的原神进程，客户端清理未确认。");

            try
            {
                if (HasRunningProcess(Path.GetFileNameWithoutExtension(betterGiProgram)) || _instances.IsBusy(betterGiProgram))
                    return CleanupFailed("BetterGI 可能仍在运行，原神客户端清理未确认。");
                if (_ownedClient is { } owned && !WindowsProcessSupervisor.TerminateOwnedProcess(owned))
                    return CleanupFailed("未能确认本次启动的原神客户端已退出。");
                if (HasRunningProcess(ClientProcessName))
                    return CleanupFailed("仍有不属于本次批次的原神客户端运行，已保留该客户端。");
                return LoginSwitchOutcome.Success(LoginSwitchStage.Done, "已确认没有运行中的原神客户端。");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                return CleanupFailed("无法核对原神或 BetterGI 的运行状态，客户端清理未确认。");
            }
        }
    }

    public async Task RestartOwnedClientAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stopped = StopOwnedClient();
        if (!stopped.Ok) throw new InvalidOperationException(stopped.Detail);
        lock (_clientGate)
        {
            if (_closed) throw new InvalidOperationException("原神桌面会话已经关闭。");
            _ownedClient?.Dispose();
            _ownedClient = null;
            _client = null;
            _launchUnverified = false;
            _active = true;
        }
        await PrepareAsync(timeout, ct).ConfigureAwait(false);
    }

    public void Close()
    {
        lock (_clientGate)
        {
            _closed = true;
            DisableInput();
            _ownedClient?.Dispose();
            _ownedClient = null;
        }
    }

    private void DisableInput()
    {
        _active = false;
        _current = null;
        _pixels = null;
    }

    private static LoginSwitchOutcome CleanupFailed(string detail) =>
        LoginSwitchOutcome.Fail(LoginSwitchStage.Failed, detail) with { Fatal = true };

    internal static bool HasRunningClient() => HasRunningProcess(ClientProcessName);

    private static bool HasRunningProcess(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes) process.Dispose();
        return processes.Length != 0;
    }

    public void VerifyHandoffWindow(StarRailWindowInfo window)
    {
        EnsureDesktopAvailable();
        using var current = Process.GetCurrentProcess();
        // BetterGI can attach to the international or cloud client as well as the CN client.
        string[] candidates = [ClientProcessName, "GenshinImpact", "Genshin Impact Cloud Game", "Genshin Impact Cloud"];
        var clients = candidates.SelectMany(Process.GetProcessesByName).ToArray();
        try
        {
            var session = clients.Where(client => client.SessionId == current.SessionId).ToArray();
            if (session.Length != 1 || session[0].Id != window.ProcessId || FindForeground() != window)
                throw new InvalidOperationException("交接 BetterGI 前需要前台只有一个可核对的原神客户端。");
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
        }
    }

    public void RestoreForeground(StarRailWindowInfo window)
    {
        var foreground = WindowsNative.GetForegroundWindow();
        if (foreground == window.Handle || _client is not { } client || !_active ||
            !IsVerifiedClientWindow(window.Handle, client))
            return;
        WindowsNative.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (foreground != IntPtr.Zero && foreground != GetShellWindow() && foregroundPid != Environment.ProcessId)
            return;

        if (WindowsNative.IsIconic(window.Handle)) WindowsNative.ShowWindow(window.Handle, WindowsNative.SwRestore);
        WindowsNative.SetForegroundWindow(window.Handle);
    }

    public async Task<GenshinDesktopFrame> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var observed = Stopwatch.GetTimestamp();
        _pixels = null;
        var window = _active ? FindForeground() : null;
        var view = new GenshinDesktopFrame(window, GenshinView.Unknown, [], null);
        if (window is not null)
        {
            var (frame, check) = ScreenCapture.CaptureChecked(window, ClientProcessName);
            var capture = CaptureVerdict.Evaluate(check);
            if (frame is not null && frame.Width == window.ClientWidth && frame.Height == window.ClientHeight && capture.Accept)
            {
                var ocr = await WindowsOcrReader.ReadAsync(frame, ct).ConfigureAwait(false);
                if (!ocr.Available) throw new InvalidOperationException(ocr.Error ?? "原神登录需要中文本地 OCR。");
                view = await ClassifyAsync(window, frame, ocr.Lines, ct).ConfigureAwait(false);
                _pixels = frame;
            }
            else view = view with { CaptureFailure = capture.Accept ? LoginScanFailure.CaptureFailed : capture.Failure };
        }
        if (!_active || FindForeground() != window) view = view with { Window = null };
        _readAt = observed;
        return _current = view;
    }

    public bool TryInput(GenshinDesktopFrame frame, GenshinInput input, ScreenRegion? point)
    {
        var latest = Interlocked.Exchange(ref _current, null);
        if (!_active || !ReferenceEquals(frame, latest) || frame.Window is not { } window ||
            Stopwatch.GetElapsedTime(_readAt) > TimeSpan.FromSeconds(3) || FindForeground() != window ||
            _instances.IsBusy(betterGiProgram) || StarRailWindow.IsProcessRunning())
            return false;
        if (point is null || point.X < 0 || point.Y < 0 || point.Right > window.ClientWidth ||
            point.Bottom > window.ClientHeight || !StillMatches(window, point))
            return false;

        if (input == GenshinInput.OpenMenu)
            return FindForeground() == window && SendPair(Key(0), Key(2));
        var target = new WindowsNative.Point((int)(point.X + point.Width / 2), (int)(point.Y + point.Height / 2));
        if (!WindowsNative.ClientToScreen(window.Handle, ref target) || !Owns(window.Handle, target) ||
            !WindowsNative.SetCursorPos(target.X, target.Y) || !GetCursorPos(out var actual) ||
            actual.X != target.X || actual.Y != target.Y || !Owns(window.Handle, target) || FindForeground() != window)
            return false;
        return SendPair(Mouse(WindowsNative.MouseEventLeftDown), Mouse(WindowsNative.MouseEventLeftUp));
    }

    private bool StillMatches(StarRailWindowInfo window, ScreenRegion point)
    {
        if (_pixels is not { } before) return false;
        var x = (int)point.X;
        var y = (int)point.Y;
        var width = Math.Max(1, (int)point.Width);
        var height = Math.Max(1, (int)point.Height);
        var now = ScreenCapture.CaptureRegion(window.OriginX + x, window.OriginY + y, width, height, "input-recheck");
        if (now is null) return false;
        var changed = 0;
        for (var dy = 0; dy < height; dy++)
        for (var dx = 0; dx < width; dx++)
        {
            var oldIndex = ((y + dy) * before.Width + x + dx) * 4;
            var newIndex = (dy * width + dx) * 4;
            if (Math.Abs(before.Bgra[oldIndex] - now.Bgra[newIndex]) > 45 ||
                Math.Abs(before.Bgra[oldIndex + 1] - now.Bgra[newIndex + 1]) > 45 ||
                Math.Abs(before.Bgra[oldIndex + 2] - now.Bgra[newIndex + 2]) > 45)
                changed++;
        }
        return changed * 10 <= width * height;
    }

    private async Task<GenshinDesktopFrame> ClassifyAsync(StarRailWindowInfo window, CapturedFrame frame,
        IReadOnlyList<RecognizedText> lines, CancellationToken ct)
    {
        var scale = frame.Height / 1080d;
        ScreenRegion Region(double x, double y, double w, double h) => new(x * scale, y * scale, w * scale, h * scale);
        ScreenRegion? Find(string file, ScreenRegion search) => templates.Find(frame, file, search, ct);
        GenshinDesktopFrame View(GenshinView view, ScreenRegion? main = null, ScreenRegion? secondary = null, string? uid = null) =>
            new(window, view, [], main, secondary, uid);

        var card = GenshinLoginScreen.Analyze(frame.Width, frame.Height, lines);
        if (card.State == GenshinLoginScreenState.NeedsUser)
            return View(GenshinView.NeedsUser) with { UserActionMarker = card.UserActionMarker };
        var normalized = lines.Select(line => line with { Text = string.Concat(line.Text.Where(c => !char.IsWhiteSpace(c))) }).ToArray();
        var brand = Label(normalized, Region(835, 280, 250, 190), t =>
            t.Equals("miHoYo", StringComparison.OrdinalIgnoreCase) || t.Equals("HO", StringComparison.OrdinalIgnoreCase));
        if (brand is not null || normalized.Any(line => GenshinLoginScreen.NormalizeExactMask(line.Text).Length != 0))
        {
            var caretSearch = Region(1045, 440, 225, 115);
            if (normalized.Any(line => GenshinLoginScreen.IsBlockingOverlay(line.Text) ||
                !GenshinLoginScreen.WithinFrame(line, frame.Width, frame.Height)))
                return View(GenshinView.Unknown) with { VisualDiagnostic = "账号卡片存在弹窗或无效的识别位置。" };
            var loginPanel = Region(610, 290, 700, 510);
            if (!normalized.Any(line => line.Text == "进入游戏") &&
                templates.Find(frame, "caret.png", caretSearch, ct, flipVertically: true) is { } openCaret)
            {
                var rows = GenshinLoginScreen.AnalyzeExpandedList(frame.Width, frame.Height, lines, openCaret);
                if (rows.Count == 0)
                {
                    var panelLines = await ReadLoginPanelAsync(frame, loginPanel, ct).ConfigureAwait(false);
                    var panel = GenshinLoginScreen.Analyze(frame.Width, frame.Height, panelLines);
                    if (panel.State == GenshinLoginScreenState.NeedsUser)
                        return View(GenshinView.NeedsUser) with { UserActionMarker = panel.UserActionMarker };
                    rows = GenshinLoginScreen.AnalyzeExpandedList(frame.Width, frame.Height, panelLines, openCaret);
                    if (rows.Count == 0 && panel.State == GenshinLoginScreenState.Unrecognized)
                    {
                        // Pale last-used labels often disappear from OCR. The contrast pass supplies only captions.
                        var captions = await ReadLoginPanelAsync(frame, loginPanel, ct, enhanceContrast: true).ConfigureAwait(false);
                        rows = GenshinLoginScreen.AnalyzeExpandedList(frame.Width, frame.Height, lines, openCaret, captions);
                        if (rows.Count == 0)
                            rows = GenshinLoginScreen.AnalyzeExpandedList(frame.Width, frame.Height, panelLines, openCaret, captions);
                    }
                }
                if (rows.Count > 0)
                    return new(window, GenshinView.Dropdown, rows, null);
                return View(GenshinView.Unknown) with { VisualDiagnostic = "已核对展开的下拉箭头，保存账号列表未能核对。" };
            }
            if (Find("caret.png", caretSearch) is { } closedCaret)
            {
                var liveCard = GenshinLoginScreen.Analyze(frame.Width, frame.Height, lines, closedCaret);
                if (liveCard.State == GenshinLoginScreenState.Unrecognized)
                {
                    var panelLines = await ReadLoginPanelAsync(frame, loginPanel, ct).ConfigureAwait(false);
                    liveCard = GenshinLoginScreen.Analyze(frame.Width, frame.Height, panelLines, closedCaret);
                }
                if (liveCard.State == GenshinLoginScreenState.NeedsUser)
                    return View(GenshinView.NeedsUser) with { UserActionMarker = liveCard.UserActionMarker };
                if (liveCard.Account is { } row)
                    return new(window, GenshinView.Card, [row], liveCard.EnterLabel, closedCaret);
                return View(GenshinView.Unknown) with { VisualDiagnostic = "已核对收起的下拉箭头，账号卡片文字或身份未能核对。" };
            }
            return View(GenshinView.Unknown) with { VisualDiagnostic = "已发现保存账号卡片文字，未能核对下拉箭头。" };
        }

        var dialog = Region(675, 365, 570, 370);
        var leave = Label(normalized, dialog, t => t == "退出至登录界面");
        if (leave is not null) return View(GenshinView.LeaveWorld, leave);
        var confirmLogout = Label(normalized, dialog, t => t.Contains("退出登录", StringComparison.Ordinal) ||
            t.Contains("退出当前账号", StringComparison.Ordinal));
        if (confirmLogout is not null && Find("quit.png", dialog) is { } quit)
            return View(GenshinView.ConfirmLogout, quit);

        var titleRegion = Region(805, 930, 310, 150);
        if (Label(normalized, titleRegion, IsUpdateCheckText) is not null)
            return View(GenshinView.CheckingUpdates);

        var gate = Label(normalized, Region(805, 945, 310, 135), IsGateText);
        var logoutRegion = GateLogoutSearch(frame.Height);
        if (gate is not null)
        {
            if (Find("logout.png", logoutRegion) is { } logout)
                return View(GenshinView.Gate, gate, logout);
            return View(GenshinView.Unknown) with { VisualDiagnostic = "已识别大门文字，退出账号图标尚未核对。" };
        }
        if (normalized.Any(line => line.Text is "确定" or "确认" or "取消" ||
            line.Text.Contains("退出", StringComparison.Ordinal) || line.Text.Contains("网络", StringComparison.Ordinal)))
            return View(GenshinView.Unknown);

        if (Find("paimon_cancel.png", Region(0, 0, 120, 120)) is not null &&
            HasMenuContent(normalized, Region(0, 190, 650, 790)))
            return View(GenshinView.Menu, Region(38, 1012, 24, 24));
        if (Find("paimon_menu.png", Region(0, 0, 150, 150)) is not { } paimon)
        {
            var titleSeen = normalized.Any(line => IsGateText(line.Text));
            var detail = titleSeen ? "检测到大门文字，但位置或唯一性无法核对。"
                : Find("logout.png", logoutRegion) is not null ? "已核对退出账号图标，大门文字未识别。"
                : null;
            return View(GenshinView.Unknown) with { VisualDiagnostic = detail };
        }

        var hud = Region(1500, 1000, 420, 80);
        var uid = GameUidParser.Find(normalized.Where(line => hud.Contains(line.CenterX, line.CenterY)));
        return View(GenshinView.World, paimon, uid: uid);
    }

    private static bool IsGateText(string text) => text is "点击进入" or "点击进入游戏";

    private static async Task<IReadOnlyList<RecognizedText>> ReadLoginPanelAsync(CapturedFrame frame, ScreenRegion panel,
        CancellationToken ct, bool enhanceContrast = false)
    {
        var x = (int)panel.X;
        var y = (int)panel.Y;
        const int magnification = 2;
        var card = frame.Crop(x, y, (int)panel.Width, (int)panel.Height).Scale(magnification);
        if (enhanceContrast)
        {
            for (var i = 0; i < card.Bgra.Length; i += 4)
            {
                var gray = (card.Bgra[i] + 2 * card.Bgra[i + 1] + card.Bgra[i + 2]) / 4;
                var value = (byte)Math.Clamp((gray - 170) * 255 / 50, 0, 255);
                card.Bgra[i] = card.Bgra[i + 1] = card.Bgra[i + 2] = value;
            }
        }
        var read = await WindowsOcrReader.ReadAsync(card, ct).ConfigureAwait(false);
        return read.Lines.Select(line => new RecognizedText(line.Text, x + line.X / magnification,
            y + line.Y / magnification, line.Width / magnification, line.Height / magnification)).ToArray();
    }

    internal static bool IsUpdateCheckText(string text) => text.TrimEnd('.', '。', '…') == "检查更新中";

    internal static ScreenRegion GateLogoutSearch(int frameHeight)
    {
        var scale = frameHeight / 1080d;
        return new(1740 * scale, 850 * scale, 180 * scale, 230 * scale);
    }

    internal static bool HasMenuContent(IEnumerable<RecognizedText> lines, ScreenRegion region) =>
        lines.Any(line => region.Contains(line.CenterX, line.CenterY) && line.Text is "背包" or "图鉴" or "好友");

    private static ScreenRegion? Label(IEnumerable<RecognizedText> lines, ScreenRegion region, Func<string, bool> label)
    {
        var matches = lines.Where(line => region.Contains(line.CenterX, line.CenterY) && label(line.Text)).Take(2).ToArray();
        return matches.Length == 1 ? new(matches[0].X, matches[0].Y, matches[0].Width, matches[0].Height) : null;
    }

    private StarRailWindowInfo? FindForeground()
    {
        var hwnd = WindowsNative.GetForegroundWindow();
        return WindowsNative.IsIconic(hwnd) ? null : TryFindWindow(hwnd);
    }

    private StarRailWindowInfo? TryFindWindow(IntPtr hwnd)
    {
        WindowsNative.EnsureDpiAwareness();
        if (_client is not { } client || !IsVerifiedClientWindow(hwnd, client) ||
            !WindowsNative.GetClientRect(hwnd, out var rect) ||
            rect.Height is < 720 or > 2160 || Math.Abs(rect.Width / (double)rect.Height - 16d / 9d) > .005)
            return null;
        var origin = new WindowsNative.Point(0, 0);
        if (!WindowsNative.ClientToScreen(hwnd, ref origin)) return null;
        return new(hwnd, client.Pid, "", rect.Width, rect.Height, origin.X, origin.Y, client.Started);
    }

    private bool IsVerifiedClientWindow(IntPtr hwnd, (int Pid, DateTimeOffset Started) client)
    {
        if (hwnd == IntPtr.Zero || WindowsNative.GetAncestor(hwnd, WindowsNative.GaRoot) != hwnd)
            return false;
        WindowsNative.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != client.Pid || !MatchesExecutable(client.Pid)) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return StarRailWindow.StartTime(process) == client.Started;
        }
        catch (ArgumentException) { return false; }
    }

    private bool MatchesExecutable(int pid)
    {
        using var handle = NativeMethods.OpenProcess(0x1000, false, pid);
        return MatchesExecutable(handle);
    }

    private bool MatchesExecutable(SafeProcessHandle handle)
    {
        var path = new StringBuilder(32768);
        var length = (uint)path.Capacity;
        return !handle.IsInvalid && QueryFullProcessImageName(handle, 0, path, ref length) &&
            string.Equals(path.ToString(), gamePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Owns(IntPtr hwnd, WindowsNative.Point point) =>
        WindowsNative.GetAncestor(WindowsNative.WindowFromPoint(point), WindowsNative.GaRoot) == hwnd;

    private static bool SendPair(NativeInput down, NativeInput up)
    {
        var size = Marshal.SizeOf<NativeInput>();
        var sent = SendInput(2, [down, up], size);
        if (sent == 1) SendInput(1, [up], size);
        return sent == 2;
    }

    private static NativeInput Key(uint flags, ushort key = 0x1B) => new() { Type = 1, Value = new() { Keyboard = new() { Key = key, Flags = flags } } };
    private static NativeInput Mouse(uint flags) => new() { Value = new() { Mouse = new() { Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public NativeInputValue Value; }
    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputValue
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WindowsNative.Point point);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
}
