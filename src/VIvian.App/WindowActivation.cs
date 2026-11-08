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
    private readonly EventWaitHandle _ack;
    private readonly RegisteredWaitHandle _registration;

    private WindowActivation(EventWaitHandle signal, EventWaitHandle ack, Window window)
    {
        _signal = signal;
        _ack = ack;
        _registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            window.Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
                window.Topmost = true;  // briefly, so it surfaces above full-screen-ish windows
                window.Topmost = false;
                window.Focus();
                // Only a responsive UI thread reaches this line: the second launch uses it to tell a visible,
                // working window apart from a hung or windowless process that merely holds the lock.
                try { _ack.Set(); } catch (ObjectDisposedException) { }
            }), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static string Name(string root) => @"Local\VIvian-Activate-" + Hash(root);
    private static string AckName(string root) => @"Local\VIvian-ActivateAck-" + Hash(root);
    private static string Hash(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..24];

    public static WindowActivation? Listen(string root, Window window)
    {
        try
        {
            return new WindowActivation(new EventWaitHandle(false, EventResetMode.AutoReset, Name(root)),
                new EventWaitHandle(false, EventResetMode.AutoReset, AckName(root)), window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        { return null; }
    }

    public enum SignalResult { NoListener, Shown, NoResponse }

    /// <summary>
    /// Asks the running instance to show itself and waits briefly for its UI thread to confirm. A lock holder that
    /// never confirms (hung, windowless, or an older build) is reported instead of exiting silently.
    /// </summary>
    public static SignalResult SignalAndWait(string root, TimeSpan timeout)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Name(root), out var existing)) return SignalResult.NoListener;
            using (existing)
            using (var ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckName(root)))
            {
                ack.Reset();
                AllowSetForegroundWindow(-1);
                if (!existing.Set()) return SignalResult.NoListener;
                return ack.WaitOne(timeout) ? SignalResult.Shown : SignalResult.NoResponse;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        { return SignalResult.NoListener; }
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

    public void Dispose() { _registration.Unregister(null); _signal.Dispose(); _ack.Dispose(); }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
