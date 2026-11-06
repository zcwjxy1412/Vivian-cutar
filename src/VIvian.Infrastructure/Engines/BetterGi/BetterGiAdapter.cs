using System.Collections.Concurrent;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Simulation;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>
/// Keeps at most one BetterGI run per installation inside this VIvian process. A slot whose cleanup was not confirmed
/// stays taken for the rest of the session: what that run left behind is unknown.
/// </summary>
public sealed class BetterGiRunRegistry
{
    public static BetterGiRunRegistry Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Guid> _active = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBusy(string installRoot) => _active.ContainsKey(Normalize(installRoot));

    internal bool TryAcquire(string installRoot, Guid runId) => _active.TryAdd(Normalize(installRoot), runId);

    internal void Release(string installRoot, Guid runId, CleanupState cleanup)
    {
        if (cleanup is CleanupState.Confirmed or CleanupState.NotRequired)
            _active.TryRemove(new KeyValuePair<string, Guid>(Normalize(installRoot), runId));
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}

/// <summary>Detects (never touches) running BetterGI processes. Detection only blocks; it never grants ownership.</summary>
public interface IBetterGiProcessProbe
{
    /// <summary>Number of running processes of this BetterGI program (or whose path cannot be read).</summary>
    int CountRunning(string programPath);
}

/// <summary>
/// Reuses the repository's single detection-only lookup (<see cref="FakeEngineAdapter.ForeignProcessIds"/>, the only
/// place the process-safety guard allows a by-name lookup). A process whose path cannot be read — for example an
/// elevated BetterGI seen from a non-elevated VIvian — counts as running. A BetterGI copy at another path is not
/// counted; BetterGI's own single-instance check then makes the new process exit with 0xFFFF without running a task.
/// </summary>
public sealed class BetterGiProcessProbe : IBetterGiProcessProbe
{
    public int CountRunning(string programPath) => FakeEngineAdapter.ForeignProcessIds(programPath).Count;
}

/// <summary>
/// BetterGI 0.66.0 adapter (Genshin Impact). It implements the full lifecycle — read-only installation check,
/// allowlisted arguments, launch through VIvian's supervisor, run-attributed incremental log evidence, conservative
/// stop and reconciliation — but real execution stays closed:
/// <list type="number">
/// <item>The scheduler blocks every non-simulation adapter before preflight.</item>
/// <item><see cref="PreflightAsync"/> refuses while the build policy or this release's launch contract is unverified.</item>
/// <item><see cref="StartAsync"/> checks the same gate first and throws without creating any process.</item>
/// </list>
/// The lifecycle behind the gate is reachable only through the internal <see cref="LaunchAsync"/>, which tests drive
/// with fake process groups and synthetic logs.
/// </summary>
public sealed class BetterGiAdapter : IGameAdapter
{
    public const string RuleVersionValue = "bgi-v0.66.0-m4-r1";

    private readonly Func<string?> _installRoot;
    private readonly BetterGiTaskRequest _request;
    private readonly IBetterGiLogSource _logs;
    private readonly IBetterGiProcessProbe _probe;
    private readonly BetterGiRunRegistry _registry;
    private readonly BetterGiRunOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<string?, CancellationToken, Task<EngineReadinessReport>> _inspect;

    public BetterGiAdapter(Func<string?> installRoot, BetterGiTaskRequest request)
        : this(installRoot, request, new BetterGiLogFileSource(), new BetterGiProcessProbe(), BetterGiRunRegistry.Shared,
            new BetterGiRunOptions(), TimeProvider.System, inspect: null)
    {
    }

    internal BetterGiAdapter(Func<string?> installRoot, BetterGiTaskRequest request, IBetterGiLogSource logs,
        IBetterGiProcessProbe probe, BetterGiRunRegistry registry, BetterGiRunOptions options, TimeProvider time,
        Func<string?, CancellationToken, Task<EngineReadinessReport>>? inspect)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(request);
        (_installRoot, _request, _logs, _probe, _registry, _options, _time) = (installRoot, request, logs, probe, registry, options, time);
        _inspect = inspect ?? ((root, ct) => BetterGiInstallationInspector.InspectAsync(root, time, ct));
    }

    public GameId Game => GameId.Genshin;
    public string RuleVersion => RuleVersionValue;
    public bool IsSimulation => false;
    public EngineProfile Profile => EngineCatalog.BetterGi;
    public BetterGiTaskRequest Request => _request;

    /// <summary>
    /// Real execution needs the build policy and this release's verified launch contract. Both are false in this
    /// build, and there is no way to change them at run time.
    /// </summary>
    public static bool ExecutionGateOpen => RealExecutionPolicy.BuildPermitsRealExecution && BetterGiContract.LaunchContractVerified;

    public async Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct)
    {
        var root = _installRoot();
        var report = await _inspect(root, ct).ConfigureAwait(false);
        if (report.Compatibility is not (CompatibilityClass.PendingValidation or CompatibilityClass.IdentityVerified))
        {
            var failed = report.Checks.FirstOrDefault(c => c.State == CheckState.Failed)
                ?? report.Checks.FirstOrDefault(c => c.State == CheckState.Warning);
            var reason = report.Compatibility is CompatibilityClass.NotConfigured or CompatibilityClass.NotInstalled
                ? ReasonCodes.NotConfigured : ReasonCodes.PreflightError;
            return PreflightResult.Block(reason, failed?.Message ?? $"BetterGI 安装检查未通过（{report.Compatibility}）。", blocksQueue: false);
        }
        var directory = Path.GetDirectoryName(report.ProgramPath!)!;
        if (_registry.IsBusy(directory))
            return PreflightResult.Block(ReasonCodes.ForeignProcess, "该 BetterGI 安装仍有一次运行未结束或未确认清理，不能重复启动。");
        if (_probe.CountRunning(report.ProgramPath!) > 0)
            return PreflightResult.Block(ReasonCodes.ForeignProcess,
                "发现已在运行的 BetterGI（单实例程序，可能来自用户或之前的运行）。VIvian 不会关闭它，已停止队列。");
        if (!ExecutionGateOpen)
            return PreflightResult.Block(ReasonCodes.RealAdapterUnverified,
                $"BetterGI {BetterGiContract.PinnedTag} 的启动、进程归属与完成证据尚未在 Windows 桌面验证，真实执行保持关闭。", blocksQueue: false);
        return PreflightResult.Ok();
    }

    public Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        if (!ExecutionGateOpen)
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, nothingStarted: true,
                $"BetterGI {BetterGiContract.PinnedTag} 的真实执行尚未开放，未启动任何进程。");
        return LaunchAsync(context, supervisor, ct);
    }

    /// <summary>
    /// The lifecycle behind the gate. Builds the allowlisted launch, takes the per-installation run slot, takes the
    /// log baseline, then starts BetterGI through the run's supervisor. Every failure before the supervisor call is
    /// reported as "nothing started"; a supervisor failure keeps its own certainty.
    /// </summary>
    internal async Task<IEngineRun> LaunchAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        var root = _installRoot();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new EngineStartException(ReasonCodes.NotConfigured, nothingStarted: true, "未配置 BetterGI 安装目录。");
        var directory = Path.GetFullPath(root.Trim().Trim('"'));
        var program = Path.Combine(directory, BetterGiContract.ProgramName);
        var (exists, link) = await ReadOnlyFileAccess.BoundedAsync(
            () => (File.Exists(program), File.Exists(program) ? ReadOnlyFileAccess.FindReparsePoint(program) : null),
            ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        if (!exists) throw new EngineStartException(ReasonCodes.NotConfigured, nothingStarted: true, "安装目录中没有 BetterGI.exe。");
        if (link is not null) throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "BetterGI.exe 路径中含有链接，拒绝启动。");
        if (program.Contains('"')) throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "安装路径包含引号，拒绝启动。");

        if (!_registry.TryAcquire(directory, context.RunId))
            throw new EngineStartException(ReasonCodes.ForeignProcess, nothingStarted: true, "该 BetterGI 安装已有一次运行，不能重复启动。");

        IBetterGiLogSubscription log;
        try
        {
            log = _logs.Open(directory);
        }
        catch (Exception ex)
        {
            _registry.Release(directory, context.RunId, CleanupState.NotRequired);
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, $"无法建立 BetterGI 日志读取基线（{ex.GetType().Name}），未启动。");
        }

        var spec = new ProcessLaunchSpec(program, _request.ToArguments(), directory);
        ISupervisedProcessGroup group;
        try
        {
            group = await supervisor.StartAsync(spec, context.RunId, ct).ConfigureAwait(false);
        }
        catch (EngineStartException ex)
        {
            await log.DisposeAsync().ConfigureAwait(false);
            if (ex.NothingStarted) _registry.Release(directory, context.RunId, CleanupState.NotRequired);
            throw;
        }
        catch (Exception)
        {
            await log.DisposeAsync().ConfigureAwait(false);
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "进程监管器启动 BetterGI 失败，无法确认没有进程被创建。");
        }
        if (group.RunId != context.RunId)
        {
            await log.DisposeAsync().ConfigureAwait(false);
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "进程组的 RunId 与本次运行不一致。");
        }
        return new BetterGiRun(context.RunId, group, log, _request, _options, _time,
            cleanup => _registry.Release(directory, context.RunId, cleanup));
    }
}
