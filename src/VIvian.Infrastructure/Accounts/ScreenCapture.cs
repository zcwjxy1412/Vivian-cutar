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
