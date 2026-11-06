using System.Collections.Concurrent;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Simulation;

namespace VIvian.Infrastructure.Engines.OneDragon;

/// <summary>
/// At most one OneDragon run per installation inside this VIvian process. A slot whose cleanup was not confirmed stays
/// taken for the rest of the session.
/// </summary>
public sealed class OneDragonRunRegistry
{
    public static OneDragonRunRegistry Shared { get; } = new();

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

/// <summary>Detects (never touches) running OneDragon launchers. Detection only blocks; it never grants ownership.</summary>
public interface IOneDragonProcessProbe
{
    int CountRunning(string programPath);
}

/// <summary>
/// Reuses the repository's single detection-only lookup (<see cref="FakeEngineAdapter.ForeignProcessIds"/>); a process
/// whose path cannot be read (for example an elevated launcher) counts as running.
/// </summary>
public sealed class OneDragonProcessProbe : IOneDragonProcessProbe
{
    public int CountRunning(string programPath) =>
        FakeEngineAdapter.ForeignProcessIds(programPath).Count
        + FakeEngineAdapter.ForeignProcessIds(Path.Combine(Path.GetDirectoryName(programPath)!, OneDragonContract.RawLauncherProgram)).Count;
}

/// <summary>
/// ZenlessZoneZero-OneDragon v2.6.0 adapter (integrated runtime). Full lifecycle behind a closed gate:
/// <list type="number">
/// <item>The scheduler blocks every non-simulation adapter before preflight.</item>
/// <item><see cref="PreflightAsync"/> refuses while the build policy or this release's launch contract is unverified.</item>
/// <item><see cref="StartAsync"/> checks the same gate first and throws without creating any process.</item>
/// </list>
/// The lifecycle is reachable only through the internal <see cref="LaunchAsync"/>, used by tests with fake process groups.
/// </summary>
public sealed class OneDragonAdapter : IGameAdapter
{
    public const string RuleVersionValue = "zzzod-v2.6.0-m4-r1";

    private readonly Func<string?> _installRoot;
    private readonly OneDragonTaskRequest _request;
    private readonly IOneDragonProcessProbe _probe;
    private readonly OneDragonRunRegistry _registry;
    private readonly OneDragonRunOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<string?, CancellationToken, Task<EngineReadinessReport>> _inspect;

    public OneDragonAdapter(Func<string?> installRoot, OneDragonTaskRequest request)
        : this(installRoot, request, new OneDragonProcessProbe(), OneDragonRunRegistry.Shared, new OneDragonRunOptions(),
            TimeProvider.System, inspect: null)
    {
    }

    internal OneDragonAdapter(Func<string?> installRoot, OneDragonTaskRequest request, IOneDragonProcessProbe probe,
        OneDragonRunRegistry registry, OneDragonRunOptions options, TimeProvider time,
        Func<string?, CancellationToken, Task<EngineReadinessReport>>? inspect)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(request);
        (_installRoot, _request, _probe, _registry, _options, _time) = (installRoot, request, probe, registry, options, time);
        _inspect = inspect ?? ((root, ct) => OneDragonInstallationInspector.InspectAsync(root, time, ct));
    }

    public GameId Game => GameId.ZenlessZoneZero;
    public string RuleVersion => RuleVersionValue;
    public bool IsSimulation => false;
    public EngineProfile Profile => EngineCatalog.OneDragon;
    public OneDragonTaskRequest Request => _request;

    public static bool ExecutionGateOpen => RealExecutionPolicy.BuildPermitsRealExecution && OneDragonContract.LaunchContractVerified;

    public async Task<PreflightResult> PreflightAsync(RunContext context, CancellationToken ct)
    {
        var report = await _inspect(_installRoot(), ct).ConfigureAwait(false);
        if (report.Compatibility is not (CompatibilityClass.PendingValidation or CompatibilityClass.IdentityVerified)
            || report.ProgramPath is null)
        {
            var failed = report.Checks.FirstOrDefault(c => c.State == CheckState.Failed)
                ?? report.Checks.FirstOrDefault(c => c.State == CheckState.Warning);
            var reason = report.Compatibility is CompatibilityClass.NotConfigured or CompatibilityClass.NotInstalled
                ? ReasonCodes.NotConfigured : ReasonCodes.PreflightError;
            return PreflightResult.Block(reason, failed?.Message ?? $"OneDragon 安装检查未通过（{report.Compatibility}）。", blocksQueue: false);
        }
        var directory = Path.GetDirectoryName(report.ProgramPath)!;
        if (_registry.IsBusy(directory))
            return PreflightResult.Block(ReasonCodes.ForeignProcess, "该 OneDragon 安装仍有一次运行未结束或未确认清理，不能重复启动。");
        if (_probe.CountRunning(report.ProgramPath) > 0)
            return PreflightResult.Block(ReasonCodes.ForeignProcess,
                "发现已在运行的 OneDragon 启动器（可能来自用户或之前的运行）。VIvian 不会关闭它，已停止队列。");
        if (!ExecutionGateOpen)
            return PreflightResult.Block(ReasonCodes.RealAdapterUnverified,
                $"OneDragon {OneDragonContract.PinnedTag} 的启动、进程归属、代码自动更新与完成证据尚未在 Windows 桌面验证，真实执行保持关闭。", blocksQueue: false);
        return PreflightResult.Ok();
    }

    public Task<IEngineRun> StartAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        if (!ExecutionGateOpen)
            throw new EngineStartException(ReasonCodes.RealAdapterUnverified, nothingStarted: true,
                $"OneDragon {OneDragonContract.PinnedTag} 的真实执行尚未开放，未启动任何进程。");
        return LaunchAsync(context, supervisor, ct);
    }

    internal async Task<IEngineRun> LaunchAsync(RunContext context, IProcessSupervisor supervisor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        var root = _installRoot();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root.Trim().Trim('"')))
            throw new EngineStartException(ReasonCodes.NotConfigured, nothingStarted: true, "未配置 OneDragon 安装目录。");
        var directory = Path.GetFullPath(root.Trim().Trim('"'));
        var program = Path.Combine(directory, OneDragonContract.EntryProgram);
        var (exists, link) = await ReadOnlyFileAccess.BoundedAsync(
            () => (File.Exists(program), File.Exists(program) ? ReadOnlyFileAccess.FindReparsePoint(program) : null),
            ReadOnlyFileAccess.QueryTimeout, ct).ConfigureAwait(false);
        if (!exists)
            throw new EngineStartException(ReasonCodes.NotConfigured, nothingStarted: true,
                $"安装目录中没有 {OneDragonContract.EntryProgram}（不使用原始启动器作为入口）。");
        if (link is not null) throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "启动器路径中含有链接，拒绝启动。");
        if (program.Contains('"')) throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, "安装路径包含引号，拒绝启动。");

        if (!_registry.TryAcquire(directory, context.RunId))
            throw new EngineStartException(ReasonCodes.ForeignProcess, nothingStarted: true, "该 OneDragon 安装已有一次运行，不能重复启动。");

        var spec = new ProcessLaunchSpec(program, _request.ToArguments(), directory, OneDragonContract.Environment);
        ISupervisedProcessGroup group;
        try
        {
            group = await supervisor.StartAsync(spec, context.RunId, ct).ConfigureAwait(false);
        }
        catch (EngineStartException ex)
        {
            if (ex.NothingStarted) _registry.Release(directory, context.RunId, CleanupState.NotRequired);
            throw;
        }
        catch (Exception)
        {
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "进程监管器启动 OneDragon 失败，无法确认没有进程被创建。");
        }
        if (group.RunId != context.RunId)
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "进程组的 RunId 与本次运行不一致。");
        return new OneDragonRun(context.RunId, group, _request, EngineCatalog.OneDragon.PinnedVersion, _options, _time,
            cleanup => _registry.Release(directory, context.RunId, cleanup));
    }
}
