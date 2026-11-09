using System.Diagnostics;

namespace VIvian.Infrastructure.Accounts;

/// <summary>
/// One Star Rail top-level window. <paramref name="ProcessStartedAt"/> pins the process identity: a later process
/// that happens to reuse the same PID has a different start time and is never treated as the same game.
/// </summary>
public sealed record StarRailWindowInfo(IntPtr Handle, int ProcessId, string Title, int ClientWidth,
    int ClientHeight, int OriginX, int OriginY, DateTimeOffset? ProcessStartedAt = null);

/// <summary>Seam over the static window lookup so the login state machine can be tested without a desktop.</summary>
public interface IGameWindowLocator
{
    StarRailWindowInfo? Find();
    /// <summary>True when a StarRail process exists even without a usable window (for example still loading).</summary>
    bool IsProcessRunning();
    /// <summary>True only while the exact process (PID + start time) is still alive.</summary>
    bool IsSameProcessAlive(StarRailWindowInfo window);
}

public sealed class StarRailWindowLocator : IGameWindowLocator
{
    public static StarRailWindowLocator Instance { get; } = new();
    public StarRailWindowInfo? Find() => StarRailWindow.Find();
    public bool IsProcessRunning() => StarRailWindow.IsProcessRunning();
    public bool IsSameProcessAlive(StarRailWindowInfo window) => StarRailWindow.IsSameProcessAlive(window);
}

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
                if (!WindowsNative.GetClientRect(handle, out var client)) return true;
                // A minimised window reports an empty client rectangle; it is still the game and must be found
                // so the caller can explain the problem instead of claiming the game is not running.
                var origin = new WindowsNative.Point(0, 0);
                WindowsNative.ClientToScreen(handle, ref origin);
                var title = new System.Text.StringBuilder(256);
                WindowsNative.GetWindowText(handle, title, title.Capacity);
                found = new StarRailWindowInfo(handle, process.Id, title.ToString(), client.Width,
                    client.Height, origin.X, origin.Y, StartTime(process));
                return false;
            }
            catch (InvalidOperationException) { return true; }
            catch (ArgumentException) { return true; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsRunning => Find() is not null;

    /// <summary>
    /// Detection only. A starting or exiting client may have no top-level window at all.
    /// </summary>
    public static bool IsProcessRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public static bool IsSameProcessAlive(StarRailWindowInfo window)
    {
        try
        {
            using var process = Process.GetProcessById(window.ProcessId);
            if (process.HasExited) return false;
            if (!string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            var started = StartTime(process);
            return window.ProcessStartedAt is null || started is null || started == window.ProcessStartedAt;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Start time needs only PROCESS_QUERY_LIMITED_INFORMATION, which works across integrity levels.</summary>
    public static DateTimeOffset? StartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (NotSupportedException) { return null; }
    }

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

    /// <summary>
    /// Brings the game forward for one input. The window is raised above others only while the click happens and
    /// is returned to the normal z-order afterwards, so the game never stays "always on top".
    /// </summary>
    public static bool BringToFront(IntPtr handle, bool keepRaised = false)
    {
        if (handle == IntPtr.Zero || !WindowsNative.IsWindow(handle)) return false;
        if (WindowsNative.IsIconic(handle)) WindowsNative.ShowWindow(handle, WindowsNative.SwRestore);
        WindowsNative.SetWindowPos(handle, WindowsNative.HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        WindowsNative.SetForegroundWindow(handle);
        if (!keepRaised) WindowsNative.SetWindowPos(handle, WindowsNative.HwndNotTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
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
