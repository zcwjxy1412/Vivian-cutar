using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VIvian.Infrastructure.Accounts;

/// <summary>
/// Mirrors March7thAssistant's own launch behaviour (tasks/game/starrailcontroller.py change_resolution /
/// restore_resolution): before the game process starts, its stored resolution is set to 1920x1080 (windowed when the
/// desktop is larger, as upstream decides), and the user's previous value is written back once the window exists.
/// March7thAssistant refuses to run at any other resolution, so a game started by VIvian must match what upstream
/// would have used. Only this one per-user value is touched; nothing else in the registry is read or written.
/// </summary>
public static class StarRailResolution
{
    public const string KeyCn = @"SOFTWARE\miHoYo\崩坏：星穹铁道";
    public const string KeyGlobal = @"SOFTWARE\Cognosphere\Star Rail";
    public const string ValueName = "GraphicsSettings_PCResolution_h431323223";

    public sealed record Resolution(int Width, int Height, bool IsFullScreen);

    public static Resolution? Parse(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        var text = Encoding.UTF8.GetString(data).TrimEnd('\0');
        using var doc = JsonDocument.Parse(text);
        int? width = null, height = null; bool? full = null;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            switch (p.Name.ToLowerInvariant())
            {
                case "width" when p.Value.ValueKind == JsonValueKind.Number: width = p.Value.GetInt32(); break;
                case "height" when p.Value.ValueKind == JsonValueKind.Number: height = p.Value.GetInt32(); break;
                case "isfullscreen" when p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False: full = p.Value.GetBoolean(); break;
            }
        }
        return width is null || height is null || full is null ? null : new Resolution(width.Value, height.Value, full.Value);
    }

    /// <summary>Same byte layout upstream writes: compact JSON, NUL-terminated, REG_BINARY.</summary>
    public static byte[] Serialize(Resolution value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["width"] = value.Width, ["height"] = value.Height, ["isFullScreen"] = value.IsFullScreen,
        }).Replace(",", ", ").Replace(":", ": ") + "\0");

    /// <summary>Upstream rule: full screen only when the desktop is not larger than the target.</summary>
    public static Resolution Target(int screenWidth, int screenHeight) =>
        new(1920, 1080, !(screenWidth > 1920 || screenHeight > 1080));

    /// <summary>
    /// Applies 1920x1080 and returns a handle that restores the previous value. Returns null when no change was
    /// needed or the game has never written its settings (nothing to restore, nothing changed).
    /// </summary>
    public static IDisposable? Apply(Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyCn, writable: true)
                            ?? Registry.CurrentUser.OpenSubKey(KeyGlobal, writable: true);
            if (key is null) { log?.Invoke("resolution: game registry key not found; unchanged"); return null; }
            var raw = key.GetValue(ValueName) as byte[];
            var current = Parse(raw);
            if (current is null) { log?.Invoke("resolution: no stored resolution; unchanged"); return null; }
            WindowsNative.EnsureDpiAwareness();
            var target = Target(GetSystemMetrics(0), GetSystemMetrics(1));
            if (current == target) return null;
            key.SetValue(ValueName, Serialize(target), RegistryValueKind.Binary);
            log?.Invoke($"resolution: {current.Width}x{current.Height} full={current.IsFullScreen} -> 1920x1080 full={target.IsFullScreen}");
            return new Restore(key.Name.Contains("Cognosphere", StringComparison.Ordinal) ? KeyGlobal : KeyCn, raw!, log);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or JsonException)
        {
            log?.Invoke("resolution: could not apply (" + ex.GetType().Name + "); unchanged");
            return null;
        }
    }

    private sealed class Restore(string keyPath, byte[] original, Action<string>? log) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
                key?.SetValue(ValueName, original, RegistryValueKind.Binary);
                log?.Invoke("resolution: previous value restored");
            }
            catch (Exception ex) { log?.Invoke("resolution: restore failed (" + ex.GetType().Name + ")"); }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
