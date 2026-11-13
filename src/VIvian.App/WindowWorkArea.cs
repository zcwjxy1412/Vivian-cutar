using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VIvian.App;

internal sealed class WindowWorkArea : IDisposable
{
    private readonly Window _window;
    private readonly Size _minimumDip;
    private HwndSource? _source;
    private bool _queued, _disposed;

    internal WindowWorkArea(Window window, Size minimumDip)
    {
        _window = window; _minimumDip = minimumDip;
        window.SourceInitialized += OnSourceInitialized;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
    }

    // Both origins are physical screen coordinates. Only sizes use the window's current DPI.
    internal static Rect Fit(Rect bounds, Rect work, Size minimumDip, double scale)
    {
        if (work.IsEmpty || work.Width <= 0 || work.Height <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(work));
        var width = Math.Min(work.Width, Math.Max(bounds.Width, minimumDip.Width * scale));
        var height = Math.Min(work.Height, Math.Max(bounds.Height, minimumDip.Height * scale));
        return new Rect(Math.Clamp(bounds.X, work.Left, work.Right - width),
            Math.Clamp(bounds.Y, work.Top, work.Bottom - height), width, height);
    }

    internal void Recover(bool preferOwner = false)
    {
        if (_disposed || _window.WindowState != WindowState.Normal) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var native)) return;
        var owner = preferOwner && _window.Owner is not null ? new WindowInteropHelper(_window.Owner).Handle : IntPtr.Zero;
        var monitor = MonitorFromWindow(owner != IntPtr.Zero ? owner : hwnd, 2 /* nearest */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var work = info.Work.ToRect();
        var scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
        _window.MinWidth = Math.Min(_minimumDip.Width, work.Width / scale);
        _window.MinHeight = Math.Min(_minimumDip.Height, work.Height / scale);
        var desired = native.ToRect();
        if (owner != IntPtr.Zero && GetWindowRect(owner, out var ownerBounds))
            desired = new Rect(ownerBounds.Right - desired.Width - 24 * scale, ownerBounds.Top + 72 * scale, desired.Width, desired.Height);
        var fit = Fit(desired, work, _minimumDip, scale);
        if (fit == native.ToRect()) return;
        // Geometry only: never show, activate, change z-order or acquire input permission here.
        if (!SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(fit.X), (int)Math.Round(fit.Y),
            (int)Math.Floor(fit.Width), (int)Math.Floor(fit.Height), 0x0010 | 0x0004 | 0x0200))
            Serilog.Log.Warning("Could not recover window work area (Win32 {Error})", Marshal.GetLastWin32Error());
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(OnMessage);
        QueueRecovery();
    }
    private void OnStateChanged(object? sender, EventArgs e) => QueueRecovery();
    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Let WPF process its DPI suggested rectangle first, then constrain the resulting bounds.
        if (message is 0x02E0 or 0x007E or 0x001A or 0x0232) QueueRecovery();
        return IntPtr.Zero;
    }
    private void QueueRecovery()
    {
        if (_disposed || _queued) return;
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _queued = false;
            if (!_disposed) Recover();
        }));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source?.RemoveHook(OnMessage);
        _window.SourceInitialized -= OnSourceInitialized;
        _window.StateChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
