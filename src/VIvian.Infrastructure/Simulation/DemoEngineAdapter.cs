using System.Threading.Channels;
using VIvian.Core;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Simulation;

/// <summary>Scripted behaviours for the in-app demonstration queue.</summary>
public enum DemoScenario { Success, NoEvidence, Failure, Stall, CleanupUnconfirmed }

/// <summary>
/// In-process stand-in for an engine, used by the app's simulation mode. It starts no process, touches no game,
/// file or input device, and every result it produces is marked as simulation.
/// </summary>
public sealed class DemoEngineAdapter(GameId game, DemoScenario scenario, TimeSpan step) : IGameAdapter
{
    public GameId Game { get; } = game;
    public string RuleVersion => "demo-1";
    public bool IsSimulation => true;

    public Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct) => Task.FromResult(PreflightResult.Ok());

    public Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct) =>
        Task.FromResult<IEngineRun>(new DemoRun(context.RunId, scenario, step));
}

/// <summary>Supervisor for simulation mode: refuses to start anything, so the demo can never launch a process.</summary>
public sealed class RefusingProcessSupervisor : IProcessSupervisor
{
    public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct) =>
        throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "模拟模式不会启动任何进程。");
}

internal sealed class DemoRun : IEngineRun
{
    private static readonly string[] Steps = ["读取模拟配置", "模拟进入游戏", "模拟执行日常任务", "模拟领取奖励", "模拟整理结果"];
    private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>();
    private readonly TaskCompletionSource<RunVerdict> _verdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly DemoScenario _scenario;
    private readonly TimeSpan _step;

    public DemoRun(Guid runId, DemoScenario scenario, TimeSpan step)
    {
        RunId = runId;
        _scenario = scenario;
        _step = step;
        _ = Task.Run(ScriptAsync);
    }

    public Guid RunId { get; }
    public IAsyncEnumerable<RunEvent> Events => _events.Reader.ReadAllAsync();

    private void Emit(RunEventKind kind, string message) =>
        _events.Writer.TryWrite(new RunEvent(RunId, DateTimeOffset.UtcNow, kind, message));

    private void End(params EvidenceKind[] evidence)
    {
        _events.Writer.TryComplete();
        _verdict.TrySetResult(VerdictPolicy.Decide(RunId,
            evidence.Select(k => new Evidence(RunId, k, "demo", DateTimeOffset.UtcNow))));
    }

    private async Task ScriptAsync()
    {
        try
        {
            Emit(RunEventKind.Progress, "【模拟】开始。不会启动游戏或任何程序。");
            var steps = _scenario == DemoScenario.Stall ? 1 : Steps.Length;
            for (var i = 0; i < steps; i++)
            {
                await Task.Delay(_step, _stop.Token).ConfigureAwait(false);
                Emit(RunEventKind.Progress, $"【模拟】{Steps[i]}（{i + 1}/{Steps.Length}）");
            }
            switch (_scenario)
            {
                case DemoScenario.Success:
                case DemoScenario.CleanupUnconfirmed:
                    Emit(RunEventKind.Evidence, "【模拟】报告：计划任务已完成");
                    End(EvidenceKind.CompletionReported);
                    break;
                case DemoScenario.NoEvidence:
                    Emit(RunEventKind.Output, "【模拟】程序退出，但没有给出完成证据");
                    End();
                    break;
                case DemoScenario.Failure:
                    Emit(RunEventKind.Evidence, "【模拟】报告：任务失败");
                    End(EvidenceKind.FailureReported);
                    break;
                case DemoScenario.Stall:
                    Emit(RunEventKind.Output, "【模拟】此后不再输出（模拟卡死）");
                    await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Emit(RunEventKind.Output, "【模拟】已按停止请求结束");
            End(EvidenceKind.CancelReported);
        }
    }

    public Task<RunVerdict> WaitForVerdictAsync(CancellationToken ct) => _verdict.Task.WaitAsync(ct);

    public Task<StopResult> RequestStopAsync(StopMode mode, CancellationToken ct)
    {
        _stop.Cancel();
        return Task.FromResult(new StopResult(CleanupState.Pending, "demo_stop"));
    }

    public Task<CleanupState> ReconcileAsync(CancellationToken ct) => Task.FromResult(
        _scenario == DemoScenario.CleanupUnconfirmed ? CleanupState.Unconfirmed : CleanupState.Confirmed);

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _stop.Dispose();
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
