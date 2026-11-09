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

internal sealed class GenshinWindowsLoginDesktop(string gamePath, string betterGiProgram, GenshinLoginTemplates templates)
    : IGenshinLoginDesktop
{
    private const string ClientProcessName = "YuanShen";
    private readonly BetterGiProcessProbe _instances = new();
    private GenshinDesktopFrame? _current;
    private CapturedFrame? _pixels;
    private long _readAt;
    private bool _active = true;
    private (int Pid, DateTimeOffset Started)? _client;

    public async Task PrepareAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            EnsureDesktopAvailable();
            using var file = ReadOnlyFileAccess.OpenForStableRead(gamePath);
            await Task.Run(() => GenshinGameExecutable.Verify(gamePath, file), limit.Token).ConfigureAwait(false);
            limit.Token.ThrowIfCancellationRequested();
            EnsureDesktopAvailable();
            _client = FindExistingClient();
            if (_client is null)
            {
                using var started = WindowsProcessSupervisor.StartUserProcess(gamePath, Path.GetDirectoryName(gamePath)!)
                    ?? throw new InvalidOperationException("Windows 没有返回本次启动的原神进程。");
                _client = VerifyClient(started);
            }

            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                EnsureDesktopAvailable();
                if (_client != FindExistingClient())
                    throw new InvalidOperationException("原神启动进程已退出或发生变化，已停止登录。");
                if (FindForeground() is not null) return;
                using var game = Process.GetProcessById(_client.Value.Pid);
                var hwnd = game.MainWindowHandle;
                if (hwnd != IntPtr.Zero && TryFindWindow(hwnd) is not null)
                {
                    if (WindowsNative.IsIconic(hwnd)) WindowsNative.ShowWindow(hwnd, WindowsNative.SwRestore);
                    WindowsNative.SetForegroundWindow(hwnd);
                }
                await Task.Delay(450, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("未等到本次原神客户端的 16∶9 前台窗口，已停止登录。");
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

    private (int Pid, DateTimeOffset Started) VerifyClient(Process game)
    {
        using var current = Process.GetCurrentProcess();
        if (game.SessionId != current.SessionId || StarRailWindow.StartTime(game) is not { } start || !MatchesExecutable(game.Id))
            throw new InvalidOperationException("原神进程的路径、会话或启动时间无法核对。");
        return (game.Id, start);
    }

    public void Close()
    {
        _active = false;
        _current = null;
        _pixels = null;
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
            if (frame is not null && frame.Width == window.ClientWidth && frame.Height == window.ClientHeight &&
                CaptureVerdict.Evaluate(check).Accept)
            {
                var ocr = await WindowsOcrReader.ReadAsync(frame, ct).ConfigureAwait(false);
                if (!ocr.Available) throw new InvalidOperationException(ocr.Error ?? "原神登录需要中文本地 OCR。");
                view = Classify(window, frame, ocr.Lines, ct);
                _pixels = frame;
            }
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

    private GenshinDesktopFrame Classify(StarRailWindowInfo window, CapturedFrame frame, IReadOnlyList<RecognizedText> lines, CancellationToken ct)
    {
        var scale = frame.Height / 1080d;
        ScreenRegion Region(double x, double y, double w, double h) => new(x * scale, y * scale, w * scale, h * scale);
        ScreenRegion? Find(string file, ScreenRegion search) => templates.Find(frame, file, search, ct);
        GenshinDesktopFrame View(GenshinView view, ScreenRegion? main = null, ScreenRegion? secondary = null, string? uid = null) =>
            new(window, view, [], main, secondary, uid);

        var card = GenshinLoginScreen.Analyze(frame.Width, frame.Height, lines);
        if (card.State == GenshinLoginScreenState.NeedsUser) return View(GenshinView.NeedsUser);
        var caret = Find("caret.png", Region(1045, 440, 225, 115));
        var normalized = lines.Select(line => line with { Text = string.Concat(line.Text.Where(c => !char.IsWhiteSpace(c))) }).ToArray();
        var masks = normalized.Where(line => GenshinLoginScreen.NormalizeExactMask(line.Text).Length != 0).ToArray();
        // An expanded list can cover the original button labels. Require the pinned dropdown caret and brand.
        var brand = Label(normalized, Region(835, 280, 250, 190), t => t.Equals("miHoYo", StringComparison.OrdinalIgnoreCase));
        var accountList = Region(670, 540, 565, 435);
        var listRows = masks.Where(line => accountList.Contains(line.CenterX, line.CenterY)).ToArray();
        if (brand is not null && caret is not null && listRows.Length > 0 &&
            masks.Length - listRows.Length <= 1)
        {
            var rows = listRows.OrderBy(line => line.CenterY).Select((line, index) => new LoginAccountObservation(
                line.Text, GenshinLoginScreen.NormalizeExactMask(line.Text), index, line.X, line.Y,
                line.Width, line.Height, .85, "")).ToArray();
            if (rows.Zip(rows.Skip(1)).All(pair => pair.First.Y + pair.First.Height < pair.Second.Y))
                return new(window, GenshinView.Dropdown, rows, null);
        }
        if (card is { State: GenshinLoginScreenState.SavedAccountCard, Account: { } row })
            return new(window, GenshinView.Card, [row], card.EnterLabel, caret);
        if (brand is not null || masks.Length > 0) return View(GenshinView.Unknown);

        var dialog = Region(675, 365, 570, 370);
        var leave = Label(normalized, dialog, t => t == "退出至登录界面");
        if (leave is not null) return View(GenshinView.LeaveWorld, leave);
        var confirmLogout = Label(normalized, dialog, t => t.Contains("退出登录", StringComparison.Ordinal) ||
            t.Contains("退出当前账号", StringComparison.Ordinal));
        if (confirmLogout is not null && Find("quit.png", dialog) is { } quit)
            return View(GenshinView.ConfirmLogout, quit);

        var gate = Label(normalized, Region(805, 945, 310, 135), t => t is "点击进入" or "点击进入游戏");
        if (gate is not null && Find("logout.png", Region(1740, 895, 180, 185)) is { } logout)
            return View(GenshinView.Gate, gate, logout);
        if (normalized.Any(line => line.Text is "确定" or "确认" or "取消" ||
            line.Text.Contains("退出", StringComparison.Ordinal) || line.Text.Contains("网络", StringComparison.Ordinal)))
            return View(GenshinView.Unknown);

        if (Find("paimon_cancel.png", Region(0, 0, 120, 120)) is not null &&
            Label(normalized, Region(0, 190, 650, 790), t => t is "背包" or "图鉴" or "好友") is not null)
            return View(GenshinView.Menu, Region(38, 1012, 24, 24));
        if (Find("paimon_menu.png", Region(0, 0, 150, 150)) is not { } paimon)
            return View(GenshinView.Unknown);

        var hud = Region(1500, 1000, 420, 80);
        var uid = GameUidParser.Find(normalized.Where(line => hud.Contains(line.CenterX, line.CenterY)));
        return View(GenshinView.World, paimon, uid: uid);
    }

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
        if (_client is not { } client || hwnd == IntPtr.Zero ||
            !WindowsNative.GetClientRect(hwnd, out var rect) ||
            rect.Height is < 720 or > 2160 || Math.Abs(rect.Width / (double)rect.Height - 16d / 9d) > .005)
            return null;
        var origin = new WindowsNative.Point(0, 0);
        if (!WindowsNative.ClientToScreen(hwnd, ref origin)) return null;
        WindowsNative.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != client.Pid || !MatchesExecutable(client.Pid)) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var start = StarRailWindow.StartTime(process);
            return start != client.Started ? null : new(hwnd, client.Pid, "", rect.Width, rect.Height, origin.X, origin.Y, start);
        }
        catch (ArgumentException) { return null; }
    }

    private bool MatchesExecutable(int pid)
    {
        using var handle = NativeMethods.OpenProcess(0x1000, false, pid);
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

    private static NativeInput Key(uint flags) => new() { Type = 1, Value = new() { Keyboard = new() { Key = 0x1B, Flags = flags } } };
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WindowsNative.Point point);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
}
