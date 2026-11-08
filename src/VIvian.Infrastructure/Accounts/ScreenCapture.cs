using System.Runtime.InteropServices;

namespace VIvian.Infrastructure.Accounts;

/// <summary>A BGRA32, top-down frame captured from a game window or decoded from a screenshot.</summary>
public sealed record CapturedFrame(byte[] Bgra, int Width, int Height, string Source)
{
    public int Stride => Width * 4;
}

/// <summary>Desktop capture. BitBlt is used because Unity windows often return black from PrintWindow.</summary>
public static class ScreenCapture
{
    public static CapturedFrame? CaptureClient(IntPtr window)
    {
        WindowsNative.EnsureDpiAwareness();
        if (window == IntPtr.Zero || !WindowsNative.IsWindow(window)) return null;
        if (WindowsNative.IsIconic(window)) WindowsNative.ShowWindow(window, WindowsNative.SwRestore);
        if (!WindowsNative.GetClientRect(window, out var client) || client.Width <= 0 || client.Height <= 0) return null;
        // PrintWindow renders the window even when another (possibly topmost) window covers it.
        // BitBlt reads screen pixels, so an occluded game would silently yield VIvian's UI.
        var printed = CaptureWithPrintWindow(window, client.Width, client.Height);
        if (printed is not null && !IsProbablyBlank(printed)) return printed;
        var origin = new WindowsNative.Point(0, 0);
        if (!WindowsNative.ClientToScreen(window, ref origin)) return null;
        return CaptureRegion(origin.X, origin.Y, client.Width, client.Height, "window");
    }

    /// <summary>
    /// Captures the game client and proves where the pixels came from. The window is never restored, raised or
    /// made topmost here: a normal-integrity VIvian cannot reliably do that to an elevated game, and trying would
    /// hide failures. PrintWindow is tried first because it renders the window itself; if that is blank the screen
    /// is read and nine sample points must all belong to the game window, otherwise the frame is rejected.
    /// </summary>
    public static (CapturedFrame? Frame, Core.Accounts.CaptureCheck Check) CaptureChecked(StarRailWindowInfo window)
    {
        WindowsNative.EnsureDpiAwareness();
        var handle = window.Handle;
        if (handle == IntPtr.Zero || !WindowsNative.IsWindow(handle))
            return (null, new(false, false, false, "none", 0, 0, false));
        var isGame = OwnerName(handle) is { } owner &&
                     string.Equals(owner, StarRailWindow.ProcessName, StringComparison.OrdinalIgnoreCase);
        if (!isGame) return (null, new(true, false, false, "none", 0, 0, false));
        if (WindowsNative.IsIconic(handle) ||
            !WindowsNative.GetClientRect(handle, out var client) || client.Width <= 0 || client.Height <= 0)
            return (null, new(true, true, true, "none", 0, 0, false));

        var printed = CaptureWithPrintWindow(handle, client.Width, client.Height);
        if (printed is not null && !IsProbablyBlank(printed))
            return (printed, new(true, true, false, "print", 0, 0, false));

        var origin = new WindowsNative.Point(0, 0);
        if (!WindowsNative.ClientToScreen(handle, ref origin))
            return (null, new(true, true, false, "bitblt", 0, 0, false));
        var (samples, owned, occluder) = SampleOwnership(handle, origin, client.Width, client.Height);
        var frame = CaptureRegion(origin.X, origin.Y, client.Width, client.Height, "window");
        var blank = frame is null || IsProbablyBlank(frame);
        return (frame, new(true, true, false, "bitblt", samples, owned, blank, occluder));
    }

    private static (int Samples, int Owned, string? Occluder) SampleOwnership(IntPtr handle,
        WindowsNative.Point origin, int width, int height)
    {
        var owned = 0;
        var total = 0;
        string? occluder = null;
        foreach (var fx in new[] { 0.12, 0.5, 0.88 })
        foreach (var fy in new[] { 0.12, 0.5, 0.88 })
        {
            total++;
            var point = new WindowsNative.Point(origin.X + (int)(width * fx), origin.Y + (int)(height * fy));
            var root = WindowsNative.GetAncestor(WindowsNative.WindowFromPoint(point), WindowsNative.GaRoot);
            if (root == handle) { owned++; continue; }
            occluder ??= DescribeWindow(root);
        }
        return (total, owned, occluder);
    }

    private static string? OwnerName(IntPtr handle)
    {
        WindowsNative.GetWindowThreadProcessId(handle, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return null; }
    }

    private static string DescribeWindow(IntPtr root)
    {
        if (root == IntPtr.Zero) return "未知窗口";
        WindowsNative.GetWindowThreadProcessId(root, out var pid);
        if (pid == (uint)Environment.ProcessId) return "VIvian 自身窗口";
        var name = OwnerName(root);
        return string.IsNullOrWhiteSpace(name) ? "其他窗口" : $"“{name}”窗口";
    }

    private static CapturedFrame? CaptureWithPrintWindow(IntPtr window, int width, int height)
    {
        var screen = WindowsNative.GetDC(window);
        if (screen == IntPtr.Zero) return null;
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = WindowsNative.CreateCompatibleDC(screen);
            bitmap = WindowsNative.CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            previous = WindowsNative.SelectObject(memory, bitmap);
            // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            if (!WindowsNative.PrintWindow(window, memory, 3)) return null;
            return ReadBitmap(memory, bitmap, width, height, "window-print");
        }
        finally
        {
            if (previous != IntPtr.Zero) WindowsNative.SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) WindowsNative.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) WindowsNative.DeleteDC(memory);
            WindowsNative.ReleaseDC(window, screen);
        }
    }

    private static bool IsProbablyBlank(CapturedFrame frame)
    {
        var bright = 0;
        for (var i = 0; i < frame.Bgra.Length; i += 4)
        {
            if (frame.Bgra[i] > 8 || frame.Bgra[i + 1] > 8 || frame.Bgra[i + 2] > 8) bright++;
        }
        return bright < frame.Width * frame.Height / 100;
    }

    public static CapturedFrame? CaptureRegion(int x, int y, int width, int height, string source)
    {
        WindowsNative.EnsureDpiAwareness();
        if (width <= 0 || height <= 0 || width > 10000 || height > 10000) return null;
        var screen = WindowsNative.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = WindowsNative.CreateCompatibleDC(screen);
            bitmap = WindowsNative.CreateCompatibleBitmap(screen, width, height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            previous = WindowsNative.SelectObject(memory, bitmap);
            if (!WindowsNative.BitBlt(memory, 0, 0, width, height, screen, x, y,
                    WindowsNative.Srccopy | WindowsNative.CaptureBlt))
                return null;
            return ReadBitmap(memory, bitmap, width, height, source);
        }
        finally
        {
            if (previous != IntPtr.Zero) WindowsNative.SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) WindowsNative.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) WindowsNative.DeleteDC(memory);
            WindowsNative.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static CapturedFrame? ReadBitmap(IntPtr hdc, IntPtr bitmap, int width, int height, string source)
    {
        var info = new WindowsNative.BitmapInfo
        {
            Header = new WindowsNative.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<WindowsNative.BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down rows
                Planes = 1,
                BitCount = 32,
                Compression = WindowsNative.BiRgb,
            },
        };
        var pixels = new byte[checked(width * height * 4)];
        var lines = WindowsNative.GetDIBits(hdc, bitmap, 0, (uint)height, pixels, ref info, WindowsNative.DibRgbColors);
        if (lines != height) return null;
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new CapturedFrame(pixels, width, height, source);
    }
}
