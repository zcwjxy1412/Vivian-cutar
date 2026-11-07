using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace VIvian.App;

/// <summary>Lets a second launch restore and focus the window of the instance that is already running
/// (same data directory). Only a named event is shared; nothing else crosses between the two processes.</summary>
internal sealed class WindowActivation : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    private WindowActivation(EventWaitHandle signal, Window window)
    {
        _signal = signal;
        _registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            window.Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
                window.Topmost = true;  // briefly, so it surfaces above full-screen-ish windows
                window.Topmost = false;
                window.Focus();
            }), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static string Name(string root) => @"Local\VIvian-Activate-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..24];

    public static WindowActivation? Listen(string root, Window window)
    {
        try { return new WindowActivation(new EventWaitHandle(false, EventResetMode.AutoReset, Name(root)), window); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        { return null; }
    }

    /// <summary>True when the running instance was asked to show itself.</summary>
    public static bool SignalExisting(string root)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Name(root), out var existing)) return false;
            using (existing)
            {
                AllowSetForegroundWindow(-1); // this launch was the user's action: let the running window take focus
                return existing.Set();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose() { _registration.Unregister(null); _signal.Dispose(); }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
