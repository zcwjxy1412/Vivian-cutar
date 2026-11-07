using System.Diagnostics;

namespace VIvian.Infrastructure.Accounts;

public sealed record StarRailWindowInfo(IntPtr Handle, int ProcessId, string Title, int ClientWidth,
    int ClientHeight, int OriginX, int OriginY);

/// <summary>Locates and activates the Star Rail window. No game memory or login token is read.</summary>
public static class StarRailWindow
{
    public const string ProcessName = "StarRail";

    public static StarRailWindowInfo? Find()
    {
        WindowsNative.EnsureDpiAwareness();
        StarRailWindowInfo? found = null;
        WindowsNative.EnumWindows((handle, _) =>
        {
            if (!WindowsNative.IsWindowVisible(handle)) return true;
            WindowsNative.GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)) return true;
                if (!WindowsNative.GetClientRect(handle, out var client) || client.Width <= 0 || client.Height <= 0)
                    return true;
                var origin = new WindowsNative.Point(0, 0);
                if (!WindowsNative.ClientToScreen(handle, ref origin)) return true;
                var title = new System.Text.StringBuilder(256);
                WindowsNative.GetWindowText(handle, title, title.Capacity);
                found = new StarRailWindowInfo(handle, process.Id, title.ToString(), client.Width,
                    client.Height, origin.X, origin.Y);
                return false;
            }
            catch (InvalidOperationException) { return true; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsRunning => Find() is not null;

    public static async Task<StarRailWindowInfo?> WaitForWindowAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = Find();
            if (window is not null) return window;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    public static bool BringToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !WindowsNative.IsWindow(handle)) return false;
        if (WindowsNative.IsIconic(handle)) WindowsNative.ShowWindow(handle, WindowsNative.SwRestore);
        // Keep the game above VIvian while it is captured. Demoting it to non-topmost would put it
        // behind a topmost VIvian window and BitBlt would capture the wrong pixels.
        WindowsNative.SetWindowPos(handle, WindowsNative.HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        WindowsNative.SetForegroundWindow(handle);
        return WindowsNative.GetForegroundWindow() == handle;
    }

    public static bool IsResponsive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.Responding;
        }
        catch { return false; }
    }
}
