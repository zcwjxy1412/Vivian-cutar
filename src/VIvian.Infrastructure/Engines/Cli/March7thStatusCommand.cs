using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines.Cli;

/// <summary>Runs the pinned release's non-elevated --status immediately before an explicitly approved task.</summary>
public static class March7thStatusCommand
{
    public static async Task<(March7thStatusResult Status, CleanupState Cleanup)> CheckAsync(
        IProcessSupervisor supervisor, string cli, string directory, Guid runId, CancellationToken ct)
    {
        await using var group = await supervisor.StartAsync(new ProcessLaunchSpec(cli, ["--status"], directory), runId, ct)
            .ConfigureAwait(false);
        var lines = new List<string>();
        var truncated = false;
        var drain = Task.Run(async () =>
        {
            await foreach (var line in group.OutputLines.ConfigureAwait(false))
            {
                if (lines.Count < 1000 && line.Length <= 32768) lines.Add(line);
                else truncated = true;
            }
        });
        int? exit = null;
        try { exit = await group.WaitForRootExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (TimeoutException) { /* cleanup below; no task may start */ }
        var cleanup = await group.TerminateAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        try { await drain.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (TimeoutException) { truncated = true; }
        if (cleanup != CleanupState.Confirmed || exit != 0 || truncated)
            return (March7thStatusResult.Reject(March7thStatusBlock.Other, "上游状态查询未完整结束或进程清理未确认。"), cleanup);
        return (March7thStatusContract.Parse(string.Join('\n', lines)), cleanup);
    }
}
