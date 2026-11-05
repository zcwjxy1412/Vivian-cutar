using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Engines;

/// <summary>
/// The real-engine adapter for one pinned release, in its pre-desktop state: it carries the version-specific profile
/// and readiness check, but refuses to start. The scheduler blocks non-simulation adapters before preflight; this
/// adapter is a second, independent barrier so a future wiring mistake still cannot launch upstream code.
/// </summary>
public sealed class PendingEngineAdapter(EngineProfile profile, Func<string?> installPath) : IGameAdapter
{
    public EngineProfile Profile { get; } = profile;
    public GameId Game => Profile.Game;
    public string RuleVersion => Profile.RuleVersion;
    public bool IsSimulation => false;

    public async Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct)
    {
        var report = await EngineReadinessInspector.InspectAsync(Game, installPath(), ct).ConfigureAwait(false);
        var decision = RealExecutionPolicy.Evaluate(report);
        return decision.Allowed
            ? PreflightResult.Block(ReasonCodes.RealAdapterUnverified, "适配器尚未实现启动流程。", blocksQueue: false)
            : PreflightResult.Block(decision.ReasonCode, decision.Explanation, blocksQueue: false);
    }

    public Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct) =>
        throw new EngineStartException(ReasonCodes.RealAdapterUnverified, nothingStarted: true,
            $"{Profile.EngineName} {Profile.PinnedVersion} 的真实执行尚未开放，未启动任何进程。");
}
