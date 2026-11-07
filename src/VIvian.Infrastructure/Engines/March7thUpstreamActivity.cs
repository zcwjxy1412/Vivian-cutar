using System.Text;
using System.Text.Json;
using VIvian.Infrastructure.Engines.Cli;

namespace VIvian.Infrastructure.Engines;

public enum UpstreamActivityState { Idle, Busy, Unknown }

public sealed record UpstreamActivity(UpstreamActivityState State, string Detail, string? RunId = null);

/// <summary>
/// What March7th itself records about its task workers (logs/runs/*.json and active.json, written by the elevated
/// worker). Liveness is always decided by the exact PID plus creation time from those records — the same rule
/// upstream's own effective_status uses — never by process name.
/// </summary>
public static class March7thUpstreamActivity
{
    private const int MaxRecordsScanned = 30;
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static Func<int, (bool Exists, double? StartedAt)> Observe { get; set; } = March7thWorkerExit.Observe;

    public static string RunsDirectory(string installDirectory) => Path.Combine(installDirectory, "logs", "runs");

    /// <summary>Is any March7th task worker still alive? Unknown when a record cannot be read or a process's start
    /// time cannot be observed (it might be the worker): callers must then treat the machine as possibly busy.</summary>
    public static UpstreamActivity Inspect(string installDirectory)
    {
        var runs = RunsDirectory(installDirectory);
        if (!Directory.Exists(runs)) return new(UpstreamActivityState.Idle, "星铁助手没有运行记录。");
        var unknown = (UpstreamActivity?)null;
        var active = Path.Combine(runs, "active.json");
        if (File.Exists(active))
        {
            var state = Check(active, out var id);
            if (state == WorkerExitState.Running)
                return new(UpstreamActivityState.Busy, $"星铁助手仍在运行任务（运行编号 {id}）。", id);
            if (state == WorkerExitState.Unknown)
                unknown = new(UpstreamActivityState.Unknown, "无法核实星铁助手运行锁中的进程是否已退出。", id);
        }
        IEnumerable<FileInfo> records;
        try
        {
            records = new DirectoryInfo(runs).EnumerateFiles("*.json")
                .Where(f => !f.Name.Equals("active.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxRecordsScanned).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(UpstreamActivityState.Unknown, "无法读取星铁助手的运行记录。"); }
        foreach (var record in records)
        {
            if (!IsMarkedRunning(record.FullName)) continue;
            var state = Check(record.FullName, out var id);
            if (state == WorkerExitState.Running)
                return new(UpstreamActivityState.Busy, $"星铁助手仍在运行任务（运行编号 {id}）。", id);
            if (state == WorkerExitState.Unknown)
                unknown ??= new(UpstreamActivityState.Unknown, $"无法核实运行 {id} 的进程是否已退出。", id);
        }
        return unknown ?? new(UpstreamActivityState.Idle, "星铁助手没有正在运行的任务进程。");
    }

    /// <summary>Exit state of one upstream run's worker, by the PID and creation time in its own record.</summary>
    public static WorkerExitState WorkerState(string installDirectory, string runId)
    {
        if (!March7thTaskContract.IsRunId(runId)) return WorkerExitState.Unknown;
        var path = Path.Combine(RunsDirectory(installDirectory), runId + ".json");
        return File.Exists(path) ? Check(path, out _) : WorkerExitState.Unknown;
    }

    /// <summary>
    /// Sends "stop" through the control file of this exact run — the protocol upstream's own GUI uses
    /// (utils/pause.py): the worker polls it every 0.3 s, releases keyboard and mouse at once and acknowledges with
    /// "stopping". Returns the control file path, or null when it could not be written.
    /// </summary>
    public static string? TrySendStop(string installDirectory, string runId)
        => TrySendControl(installDirectory, runId, "stop");

    /// <summary>Uses the exact-run control protocol documented by the pinned upstream release.</summary>
    public static string? TrySendControl(string installDirectory, string runId, string command)
    {
        if (command is not ("pause" or "resume" or "stop")) return null;
        if (!March7thTaskContract.IsRunId(runId)) return null;
        var runs = RunsDirectory(installDirectory);
        var control = ControlFile(runs, runId);
        if (control is null) return null;
        var temporary = Path.Combine(runs, $".vivian_control_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, "{\"command\": \"" + command + "\"}", Utf8);
            File.Move(temporary, control, overwrite: true);
            return control;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>The worker's acknowledgement ("running", "pausing", "paused", "stopping"), or null.</summary>
    public static string? ReadControlState(string controlFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(controlFile + ".state"), new JsonDocumentOptions { MaxDepth = 4 });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("state", out var s)
                && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The upstream run that holds the lock for <paramref name="task"/> and started at or after
    /// <paramref name="since"/> (used when the entry's launch line was not captured).</summary>
    public static string? ActiveRunStartedSince(string installDirectory, string task, DateTimeOffset since)
    {
        try
        {
            var active = Path.Combine(RunsDirectory(installDirectory), "active.json");
            if (!File.Exists(active)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(active), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("run_id", out var id) || id.GetString() is not { } runId || !March7thTaskContract.IsRunId(runId)
                || !root.TryGetProperty("task", out var t) || t.GetString() != task
                || !root.TryGetProperty("started_at", out var s) || !DateTimeOffset.TryParse(s.GetString(), out var started))
                return null;
            return started >= since ? runId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string? ControlFile(string runs, string runId)
    {
        var fallback = Path.Combine(runs, runId + ".control");
        try
        {
            var record = Path.Combine(runs, runId + ".json");
            if (!File.Exists(record)) return File.Exists(fallback) ? fallback : null;
            using var doc = JsonDocument.Parse(File.ReadAllText(record), new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.TryGetProperty("control_file", out var c) && c.GetString() is { Length: > 0 } declared)
            {
                // Only ever a file inside this installation's runs folder, never a path taken blindly from a record.
                var full = Path.GetFullPath(declared);
                return Path.GetDirectoryName(full) is { } dir
                    && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(runs), StringComparison.OrdinalIgnoreCase)
                    ? full : fallback;
            }
            return fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or NotSupportedException)
        { return fallback; }
    }

    private static bool IsMarkedRunning(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16 });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "running";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }

    private static WorkerExitState Check(string path, out string? runId)
    {
        runId = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return WorkerExitState.Unknown;
            runId = root.TryGetProperty("run_id", out var id) ? id.GetString() : null;
            if (!root.TryGetProperty("pid", out var p) || !p.TryGetInt32(out var pid) || pid <= 0
                || !root.TryGetProperty("pid_create_time", out var c) || !c.TryGetDouble(out var created)
                || !double.IsFinite(created) || created <= 0)
                return WorkerExitState.Unknown;
            var current = Observe(pid);
            if (!current.Exists) return WorkerExitState.Exited;
            if (current.StartedAt is null) return WorkerExitState.Unknown;
            return Math.Abs(current.StartedAt.Value - created) < 1.0 ? WorkerExitState.Running : WorkerExitState.Exited;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return WorkerExitState.Unknown; }
    }
}
