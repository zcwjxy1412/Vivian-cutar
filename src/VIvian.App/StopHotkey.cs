using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VIvian.App;

/// <summary>
/// System-wide F10 (March7th's own default stop key) while a queue is running, so a stop never depends on reaching
/// VIvian's window over a full-screen game. Registered only during a run; released as soon as it ends.
/// </summary>
internal sealed class StopHotkey : IDisposable
{
    private const int Id = 0x5643; // "VC"
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF10 = 0x79;
    private readonly HwndSource _source;
    private readonly Action _onPressed;
    private bool _registered;

    public StopHotkey(Window window, Action onPressed)
    {
        _onPressed = onPressed;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
        _source.AddHook(Hook);
    }

    public bool IsRegistered => _registered;

    /// <summary>Returns false when another program already owns F10 (e.g. March7th's own window is open).</summary>
    public bool Register()
    {
        if (!_registered) _registered = RegisterHotKey(_source.Handle, Id, ModNoRepeat, VkF10);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && UnregisterHotKey(_source.Handle, Id)) _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id) { handled = true; _onPressed(); }
        return IntPtr.Zero;
    }

    public void Dispose() { Unregister(); _source.RemoveHook(Hook); }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
