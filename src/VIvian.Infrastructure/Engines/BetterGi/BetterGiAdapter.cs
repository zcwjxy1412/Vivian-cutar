using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Simulation;

[assembly: InternalsVisibleTo("VIvian.BetterGiProbe")]

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>
/// BetterGI's IPC root is per user, so all installations share one run slot inside VIvian.
/// Unconfirmed cleanup keeps that slot occupied for the rest of the session.
/// </summary>
public sealed class BetterGiRunRegistry
{
    public static BetterGiRunRegistry Shared { get; } = new();

    private readonly Lock _gate = new();
    private Guid? _active;

    public bool IsBusy
    {
        get { lock (_gate) return _active is not null; }
    }

    internal bool TryAcquire(Guid runId)
    {
        lock (_gate)
        {
            if (_active is not null) return false;
            _active = runId;
            return true;
        }
    }

    internal void Release(Guid runId, CleanupState cleanup)
    {
        lock (_gate)
        {
            if (_active == runId && cleanup is (CleanupState.Confirmed or CleanupState.NotRequired))
                _active = null;
        }
    }
}

/// <summary>Detects (never touches) running BetterGI processes. Detection only blocks; it never grants ownership.</summary>
public interface IBetterGiProcessProbe
{
    /// <summary>Whether a BetterGI root can already receive forwarded commands.</summary>
    bool IsBusy(string programPath);
}

/// <summary>
/// Checks the same-path process and BetterGI 0.66.0's per-user IPC pipe. The pipe can belong to another installation.
/// </summary>
public sealed class BetterGiProcessProbe : IBetterGiProcessProbe
{
    public bool IsBusy(string programPath)
    {
        if (FakeEngineAdapter.ForeignProcessIds(programPath).Count > 0) return true;

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is not { } user || RootPipeMayExist($"BetterGI.v2.user-{user.Value}.root");
    }

    internal static bool RootPipeMayExist(string pipeName)
    {
        const int errorFileNotFound = 2;
        // No connection or message is sent. Busy, access denied and other ambiguous errors block startup.
        return WaitNamedPipe($@"\\.\pipe\{pipeName}", 1) || Marshal.GetLastPInvokeError() != errorFileNotFound;
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string pipeName, uint timeout);
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
    internal bool DelegateNativeTasks { get; init; }
    public const string RuleVersionValue = "bgi-v0.66.0-m6-r1";

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
        if (_registry.IsBusy)
            return PreflightResult.Block(ReasonCodes.ForeignProcess, "仍有一次 BetterGI 运行未结束或未确认清理，不能重复启动。");
        if (_probe.IsBusy(report.ProgramPath!))
            return PreflightResult.Block(ReasonCodes.ForeignProcess,
                "发现已在运行的 BetterGI（单实例程序，可能来自用户或之前的运行）。VIvian 不会关闭它，已停止队列。");
        try
        {
            await CheckLaunchPlanAsync(directory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            return PreflightResult.Block(ReasonCodes.PreflightError, ConfigurationError(ex), blocksQueue: false);
        }
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
    /// The lifecycle behind the gate. Builds the allowlisted launch, takes the BetterGI run slot, takes the
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

        BetterGiTaskRequest resolved;
        try
        {
            resolved = await CheckLaunchPlanAsync(directory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            throw new EngineStartException(ReasonCodes.PreflightError, nothingStarted: true, ConfigurationError(ex));
        }
        if (_probe.IsBusy(program))
            throw new EngineStartException(ReasonCodes.ForeignProcess, nothingStarted: true, "发现已有 BetterGI 进程或根管道，拒绝转发本次任务。");

        if (!_registry.TryAcquire(context.RunId))
            throw new EngineStartException(ReasonCodes.ForeignProcess, nothingStarted: true, "已存在一次 BetterGI 运行，不能重复启动。");

        IBetterGiLogSubscription log;
        try
        {
            log = _logs.Open(directory);
        }
        catch (Exception ex)
        {
            _registry.Release(context.RunId, CleanupState.NotRequired);
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true, $"无法建立 BetterGI 日志读取基线（{ex.GetType().Name}），未启动。");
        }

        var spec = new ProcessLaunchSpec(program, resolved.ToArguments(), directory);
        ISupervisedProcessGroup group;
        try
        {
            if (_probe.IsBusy(program))
                throw new EngineStartException(ReasonCodes.ForeignProcess, nothingStarted: true, "BetterGI 在启动前出现已有实例，拒绝转发任务。");
            group = await supervisor.StartAsync(spec, context.RunId, ct).ConfigureAwait(false);
        }
        catch (EngineStartException ex)
        {
            await log.DisposeAsync().ConfigureAwait(false);
            if (ex.NothingStarted) _registry.Release(context.RunId, CleanupState.NotRequired);
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
        return new BetterGiRun(context.RunId, group, log, resolved, _options, _time,
            cleanup => _registry.Release(context.RunId, cleanup));
    }

    private async Task<BetterGiTaskRequest> CheckLaunchPlanAsync(string directory, CancellationToken ct)
    {
        var resolved = await BetterGiConfiguration.ResolveAsync(directory, _request, ct).ConfigureAwait(false);
        if (!DelegateNativeTasks && resolved.OneDragonPlan?.EnabledTasks.Contains("自动秘境") == true)
        {
            var domain = await BetterGiConfiguration.InspectDomainResinAsync(directory, ct).ConfigureAwait(false);
            if (domain.AutoArtifactSalvage)
                throw new InvalidDataException("BetterGI 自动秘境启用了分解圣遗物，已阻止启动。");
            if (!domain.HasExplicitBudget)
                throw new InvalidDataException("BetterGI 自动秘境未指定树脂使用次数，已阻止启动。");
            if (!BetterGiContract.DomainSpendLimitVerified)
                throw new InvalidDataException("当前 BetterGI 版本无法在所有领奖界面保证树脂次数，秘境仅可保存和预览。");
            return resolved with { DomainResinPlan = domain };
        }
        return resolved;
    }

    private static bool IsConfigurationError(Exception ex) =>
        ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or TimeoutException;

    private static string ConfigurationError(Exception ex) =>
        ex is InvalidDataException ? ex.Message : $"无法确认 BetterGI 配置（{ex.GetType().Name}），已阻止启动。";
}
