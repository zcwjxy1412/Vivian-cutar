using System.Text.Json;

namespace VIvian.Infrastructure.Engines;

/// <summary>Bounded, user-editable startup limits; invalid files fail closed to conservative defaults.</summary>
public sealed record March7thCircuitOptions(int MaxResolutionFailures, TimeSpan MaxStartupWait)
{
    public static March7thCircuitOptions Default { get; } = new(2, TimeSpan.FromMinutes(3));

    public static March7thCircuitOptions Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "march7th-startup-limits.json");
        if (!File.Exists(path)) return Default;
        try
        {
            if (new FileInfo(path).Length > 4096) return Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("maxResolutionFailures", out var count) || !count.TryGetInt32(out var failures)
                || failures is < 1 or > 5 || !root.TryGetProperty("maxStartupWaitSeconds", out var wait)
                || !wait.TryGetInt32(out var seconds) || seconds is < 30 or > 900)
                return Default;
            return new(failures, TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Default; }
    }
}
