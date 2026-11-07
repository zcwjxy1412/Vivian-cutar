using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VIvian.Infrastructure.Engines;

/// <summary>A field the user edited in VIvian was also changed in the original software since VIvian read it.</summary>
public sealed class ConfigurationConflictException(string path, string title, string expected, string? actual)
    : IOException($"“{title}”已在原软件中改为“{actual ?? "无法识别"}”（你修改前是“{expected}”）。请重新读取后再决定保留哪一个；其他字段没有写入。")
{
    public string Path { get; } = path;
}

/// <summary>
/// The pinned upstream run lock: logs/runs/active.json holds run_id, pid and pid_create_time while a task runs
/// (v2026.10.31 utils/runs.py acquire_run_lock / read_active_run). The GUI alone never holds it.
/// </summary>
public static class March7thRunLock
{
    /// <summary>Run id of a task that is executing now, or null. Unreadable or unverifiable locks count as running.</summary>
    public static string? ActiveRunId(string installDirectory)
    {
        var path = Path.Combine(installDirectory, "logs", "runs", "active.json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 64_000) return "未知";
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            var runId = root.TryGetProperty("run_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "未知" : "未知";
            if (!root.TryGetProperty("pid", out var p) || !p.TryGetInt32(out var pid) || pid <= 0) return null; // stale/empty lock
            double? created = root.TryGetProperty("pid_create_time", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
            return IsAlive(pid, created) ? runId : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return "未知"; }
    }

    private static bool IsAlive(int pid, double? createdEpochSeconds)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (createdEpochSeconds is null) return true;
            try
            {
                var started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0;
                return Math.Abs(started - createdEpochSeconds.Value) < 2.0; // a reused PID started at another time
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { return true; } // elevated worker: start time not readable without elevation; assume it is the holder
        }
        catch (ArgumentException) { return false; } // no such process
        catch (InvalidOperationException) { return false; }
    }
}

public sealed record InstanceChoice(string Name, string Purpose);

/// <summary>Instance names of the installed release (assets/config/instance_names.json: type → name → purpose).</summary>
public static class March7thInstanceCatalog
{
    public static IReadOnlyDictionary<string, IReadOnlyList<InstanceChoice>>? Read(string installDirectory)
    {
        var path = Path.Combine(installDirectory, "assets", "config", "instance_names.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1_000_000) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var result = new Dictionary<string, IReadOnlyList<InstanceChoice>>();
            foreach (var type in doc.RootElement.EnumerateObject())
            {
                if (type.Value.ValueKind != JsonValueKind.Object) continue;
                var names = type.Value.EnumerateObject()
                    .Where(n => n.Value.ValueKind == JsonValueKind.String && IsSafe(n.Name)
                        && (n.Value.GetString()!.Length == 0 || IsSafe(n.Value.GetString()!)))
                    .Select(n => new InstanceChoice(n.Name, n.Value.GetString()!)).ToList();
                result[type.Name] = names;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // Names end up as YAML plain scalars and in the "a=b|c=d" option encoding.
    private static bool IsSafe(string s) => s.Length is > 0 and <= 40 && s.IndexOfAny(['|', '=', '#', ':', '"', '\'', '\n', '\r']) < 0;
}
