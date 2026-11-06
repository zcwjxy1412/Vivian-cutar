using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace VIvian.Infrastructure.Engines.Cli;

public enum WorkerExitState { Exited, Running, Unknown }

/// <summary>Checks the exact PID and creation time recorded for this upstream RunId; never searches by process name.</summary>
public static class March7thWorkerExit
{
    public static WorkerExitState Check(string output, string runId, string task) => Check(output, runId, task, Observe);

    internal static WorkerExitState Check(string output, string runId, string task,
        Func<int, (bool Exists, double? StartedAt)> observe)
    {
        var last = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).LastOrDefault(x => x.StartsWith('{'));
        if (last is null) return WorkerExitState.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(last, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("run_id", out var id) || id.GetString() != runId
                || !root.TryGetProperty("task", out var actualTask) || actualTask.GetString() != task
                || !root.TryGetProperty("ended_at", out var ended) || ended.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(ended.GetString())
                || !root.TryGetProperty("lock", out var owned) || owned.GetString() != "acquired"
                || !root.TryGetProperty("pid", out var pidValue) || !pidValue.TryGetInt32(out var pid) || pid <= 0
                || !root.TryGetProperty("pid_create_time", out var timeValue)
                || !timeValue.TryGetDouble(out var started) || !double.IsFinite(started) || started <= 0)
                return WorkerExitState.Unknown;
            var state = root.TryGetProperty("effective_status", out var effective) ? effective.GetString()
                : root.TryGetProperty("status", out var status) ? status.GetString() : null;
            if (state is not ("success" or "failed" or "stopped" or "aborted" or "interrupted"))
                return WorkerExitState.Unknown;
            var current = observe(pid);
            if (!current.Exists) return WorkerExitState.Exited;
            if (current.StartedAt is null) return WorkerExitState.Unknown;
            return Math.Abs(current.StartedAt.Value - started) < 1.0
                ? WorkerExitState.Running : WorkerExitState.Exited;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return WorkerExitState.Unknown; }
    }

    private static (bool Exists, double? StartedAt) Observe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return (false, null);
            return (true, new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0);
        }
        catch (ArgumentException) { return (false, null); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return (true, null); }
    }
}
