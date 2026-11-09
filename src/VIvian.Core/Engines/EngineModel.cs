namespace VIvian.Core.Engines;

/// <summary>
/// How far a capability is proven. Only <see cref="Verified"/> is backed by an independent check of the exact
/// pinned release; research, source code or documentation alone never reach it.
/// </summary>
public enum CapabilityStatus
{
    /// <summary>Independently checked against the pinned official release (evidence level A).</summary>
    Verified,
    /// <summary>Described by pinned source code or upstream documentation, not yet observed on the real release.</summary>
    Pending,
    /// <summary>Not offered by the pinned release, or deliberately not used by VIvian.</summary>
    Unsupported,
}

/// <summary>Evidence levels used by docs/research (A = independently verified … D = unverified / inferred).</summary>
public enum EvidenceLevel
{
    /// <summary>Independently verified on the real official release bytes or a real run.</summary>
    A,
    /// <summary>Pinned upstream source code.</summary>
    B,
    /// <summary>Upstream documentation or website.</summary>
    C,
    /// <summary>Research claim or inference not yet checked.</summary>
    D,
}

/// <summary>Capabilities VIvian negotiates with every engine. The same list is shown for all three engines.</summary>
public enum EngineCapabilityId
{
    InstallationDiscovery,
    ExecutableIdentity,
    VersionMetadata,
    OfficialReleaseVerification,
    ConfigurationPathCheck,
    VersionQuery,
    StatusQuery,
    TaskListQuery,
    TaskExecution,
    GracefulStop,
    EmergencyStop,
    CompletionEvidence,
    ProcessOwnership,
}

/// <summary>
/// One capability of one pinned engine version. <see cref="Source"/> names the document or upstream file the claim
/// comes from, so a reader can check it. A capability that launches upstream code is never executable in this build,
/// whatever its status (see <see cref="RealExecutionPolicy"/>).
/// </summary>
/// <param name="EnabledInVivian">VIvian offers this capability in the current build. Only read-only capabilities
/// (which never start upstream code) can be enabled.</param>
public sealed record EngineCapability(
    EngineCapabilityId Id, string Title, CapabilityStatus Status, EvidenceLevel Evidence,
    bool LaunchesUpstreamCode, bool EnabledInVivian, string Source, string Note);

/// <summary>
/// A versioned task identifier and its source evidence. Execution is decided separately by RealExecutionPolicy.
/// </summary>
public sealed record EngineTaskDefinition(
    string Id, string Title, CapabilityStatus Status, EvidenceLevel Evidence, string Source, string Note,
    TaskFrequency Frequency = TaskFrequency.OnDemand,
    string Invocation = "", string CompletionSignal = "", string[]? Dependencies = null,
    string[]? Preconditions = null, string[]? ParameterPaths = null);

public enum TaskFrequency { Daily, Weekly, OnDemand }
public enum ParameterAccess { VerifiedReadWrite, VerifiedReadOnly, UpstreamManaged, UnknownIncompatible }
public sealed record EngineParameterDefinition(
    string Path, string Title, string Description, string DataType, string AllowedValues,
    string DefaultValue, string Scope, string Dependencies, string TaskImpact,
    string ConfigSource, ParameterAccess Access, string Source, string Group = "常规");

public enum PrivilegeRequirement
{
    /// <summary>Runs as the standard user.</summary>
    Standard,
    /// <summary>Upstream asks for elevation (UAC). VIvian must never elevate silently.</summary>
    ElevationRequested,
    /// <summary>Not established for this release.</summary>
    Unknown,
}

public enum ProcessTopology
{
    /// <summary>The launched program does the work and exits when done.</summary>
    SingleProcess,
    /// <summary>A launcher starts a worker (for example python.exe) and may exit before it.</summary>
    LauncherWithWorker,
    /// <summary>A GUI that may restart itself elevated, leaving the original process tree.</summary>
    GuiMayRestartElevated,
    /// <summary>Not established for this release.</summary>
    Unknown,
}

public enum PathConstraint { None, AsciiWithoutSpaces }

/// <summary>What VIvian would need to launch the engine. Every field stays Pending until observed on the release.</summary>
public sealed record LaunchRequirements(
    string EntryProgram, bool WritesToStandardOutput, PrivilegeRequirement Privilege, ProcessTopology Topology,
    PathConstraint Path, CapabilityStatus Status, string OwnershipExpectation, string Note);

public enum EvidenceOutcome { Success, Failure, Cancellation, Unknown }

/// <summary>A signal that would have to be observed, for this RunId, to report the given outcome.</summary>
public sealed record EvidenceRequirement(EvidenceOutcome Outcome, string Signal, CapabilityStatus Status, EvidenceLevel Evidence, string Source);

/// <summary>
/// An official release asset. <see cref="DigestStatus"/> Verified means the digest was copied from the official
/// release metadata and recorded in docs/evidence; Pending means it is reported by research only.
/// </summary>
public sealed record OfficialReleaseAsset(string FileName, long? Length, string Sha256, CapabilityStatus DigestStatus, string Source);

/// <summary>A path inside the installation that VIvian may check for existence. It is never read or modified.</summary>
public sealed record KnownInstallPath(string RelativePath, bool IsDirectory, bool Required, string Purpose);

/// <summary>
/// Everything VIvian knows about one pinned engine release. Facts come from docs/research and docs/evidence; anything
/// not independently verified is Pending or Unsupported. This is metadata only and never authorizes execution.
/// </summary>
public sealed record EngineProfile(
    GameId Game, string EngineName, string PinnedVersion, string? PinnedCommit, string RuleVersion,
    IReadOnlyList<string> ExpectedPrograms, IReadOnlyList<KnownInstallPath> KnownPaths,
    IReadOnlyList<OfficialReleaseAsset> OfficialAssets, IReadOnlyList<EngineCapability> Capabilities,
    IReadOnlyList<EngineTaskDefinition> Tasks, LaunchRequirements Launch,
    IReadOnlyList<EvidenceRequirement> CompletionContract, IReadOnlyList<string> KnownSideEffects,
    IReadOnlyList<string> UnsupportedVersions)
{
    public EngineCapability Capability(EngineCapabilityId id) => Capabilities.Single(c => c.Id == id);
    public IReadOnlyList<EngineParameterDefinition> Parameters { get; init; } = [];
}

/// <summary>Where an engine stands on this machine, from the most to the least blocking.</summary>
public enum CompatibilityClass
{
    NotConfigured,
    NotInstalled,
    Unavailable,
    UnsafeLayout,
    UnsupportedVersion,
    UnknownVersion,
    /// <summary>Installation found and matches the pinned layout; identity and behaviour still need desktop evidence.</summary>
    PendingValidation,
    /// <summary>The program file is byte-identical to the verified official release. Behaviour is still unverified.</summary>
    IdentityVerified,
}

public enum CheckState { Passed, Warning, Failed, Pending, NotApplicable }

public enum EngineErrorCategory
{
    None,
    NotConfigured,
    PathInvalid,
    NotInstalled,
    AccessDenied,
    Timeout,
    LinkRejected,
    UnsafeLayout,
    UnsupportedVersion,
    IdentityMismatch,
    EvidencePending,
    ExecutionDisabled,
    Internal,
}

/// <summary>One readiness check with an actionable message for the user.</summary>
public sealed record ReadinessCheck(
    string Id, string Title, CheckState State, EngineErrorCategory Category, string Message, string? Action = null);

/// <summary>Result of the read-only readiness check of one engine. Never contains file contents or credentials.</summary>
public sealed record EngineReadinessReport(
    GameId Game, string EngineName, string PinnedVersion, CompatibilityClass Compatibility,
    string? DetectedVersion, string? ProgramPath, string? ProgramSha256,
    IReadOnlyList<ReadinessCheck> Checks, DateTimeOffset CheckedAt)
{
    /// <summary>Always false in this build: no real engine may run. See <see cref="RealExecutionPolicy"/>.</summary>
    public bool RealExecutionAllowed => RealExecutionPolicy.Evaluate(this).Allowed;

    public EngineErrorCategory PrimaryError =>
        Checks.FirstOrDefault(c => c.State == CheckState.Failed)?.Category ?? EngineErrorCategory.None;
}

public sealed record ExecutionDecision(bool Allowed, string ReasonCode, string Explanation);

/// <summary>
/// The single place that decides whether a real engine may be launched. Default builds never launch one.
/// Two separately stamped Windows test builds exist: the consumed M4-D one-attempt screen_test package
/// (<c>M4_SCREEN_TEST</c>) and the M4-F1 reusable manual-test build (<c>M4_MANUAL_TEST</c>). Both admit only
/// March7thAssistant <c>screen_test</c>, only after a per-run user confirmation; scheduling stays closed.
/// </summary>
public static class RealExecutionPolicy
{
#if VIvian_UNIFIED_INSTALL && !M5_DAILY_TEST
#error 合流安装版须保留现有崩铁日常构建模式。
#endif
#if VIvian_UNIFIED_INSTALL
    public static bool IsUnifiedInstall => true;
#else
    public static bool IsUnifiedInstall => false;
#endif

#if (M4_SCREEN_TEST && M4_MANUAL_TEST) || (M5_DAILY_TEST && (M4_SCREEN_TEST || M4_MANUAL_TEST))
#error 单次测试包与可重复手动实测版不能同时启用。
#endif
#if M4_SCREEN_TEST || M4_MANUAL_TEST || M5_DAILY_TEST
    public static bool BuildPermitsRealExecution => true;
#else
    public static bool BuildPermitsRealExecution => false;
#endif
#if M4_MANUAL_TEST || M5_DAILY_TEST
    /// <summary>M4-F1: each completed, reconciled run may be followed by another manually confirmed run.</summary>
    public static bool IsReusableManualTest => true;
#else
    public static bool IsReusableManualTest => false;
#endif
    /// <summary>M4-D: the whole package permits exactly one attempt (tracked by a marker file).</summary>
    public static bool IsOneShotScreenTest => BuildPermitsRealExecution && !IsReusableManualTest;

#if M5_DAILY_TEST
    public static bool IsM5DailyTest => true;
#else
    public static bool IsM5DailyTest => false;
#endif

    /// <summary>
    /// The single real task of the M5 build. Upstream "routine" = Daily.prepare_daily(ignore_refresh=True) + reward:
    /// it spends stamina (Power.run, support character) before daily practice, which "daily" alone never does —
    /// the 2026-11-06 real run of "daily" stopped at 300/500 for exactly that reason.
    /// </summary>
    public const string M5RealTask = "routine";

    public static bool AllowsTask(string task) => BuildPermitsRealExecution &&
        (IsM5DailyTest ? task == M5RealTask : task == "screen_test");

    public static ExecutionDecision Evaluate(EngineReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        // The readiness report alone never authorises a launch; the manual-test page adds the per-run checks.
        return new(false, Execution.ReasonCodes.RealAdapterUnverified,
            IsReusableManualTest
                ? "只读检查本身不授权执行：真实任务只能在“真实任务测试”页逐次确认后启动，且每次都要通过即时状态检查。"
                : BuildPermitsRealExecution
                ? "此单次测试包只允许从星铁实测页确认 screen_test，且必须通过即时状态检查。"
                : "此开发版不启动任何真实引擎：进程归属、UAC 边界与完成证据尚待 Windows 桌面实测。");
    }
}

/// <summary>Inputs for one manual real-test admission decision. Every field is re-evaluated before each run.</summary>
public sealed record ManualTestInputs(
    string? TaskId, bool UserConfirmedThisRun, bool SelectionUnchangedSinceConfirmation,
    bool ReleaseArchiveVerified, bool InstallIdentityVerified, bool QueueIdle, bool QueueRunning,
    bool HasUnresolvedCleanup, bool OneShotConsumed);

public enum ManualTestState { NotAvailable, InstallNotVerified, NeedsConfirmation, Ready, Running, CleanupUnconfirmed, Consumed }

public sealed record ManualTestAdmission(bool Allowed, ManualTestState State, string Text);

/// <summary>
/// Per-run admission for the real-test page. Pure and side-effect free so it can be unit tested. A run is admitted only
/// when the build permits it, the task is whitelisted, the installation was verified, no earlier run is still
/// unreconciled (Cleanup Unconfirmed keeps the queue blocked) and the user confirmed this exact selection.
/// </summary>
public static class ManualTestPolicy
{
    public static ManualTestAdmission Evaluate(ManualTestInputs i)
    {
        ArgumentNullException.ThrowIfNull(i);
        if (!RealExecutionPolicy.BuildPermitsRealExecution || !RealExecutionPolicy.AllowsTask(i.TaskId ?? ""))
            return new(false, ManualTestState.NotAvailable, "此版本不开放该真实任务。");
        if (RealExecutionPolicy.IsOneShotScreenTest && i.OneShotConsumed)
            return new(false, ManualTestState.Consumed, "本包的单次 screen_test 许可已使用；不会自动重试。");
        if (i.QueueRunning) return new(false, ManualTestState.Running, "正在执行");
        if (i.HasUnresolvedCleanup)
            return new(false, ManualTestState.CleanupUnconfirmed,
                "清理未确认：上一次运行的进程退出尚未确认。请先在“调度与监控”核对并处理，再开始下一次。");
        if (!i.QueueIdle) return new(false, ManualTestState.Running, "正在执行");
        if (!i.ReleaseArchiveVerified || !i.InstallIdentityVerified)
            return new(false, ManualTestState.InstallNotVerified, "未通过安装核验：请先核对官方发行包与安装目录。");
        if (!i.UserConfirmedThisRun || !i.SelectionUnchangedSinceConfirmation)
            return new(false, ManualTestState.NeedsConfirmation, "可以进行手动实测：请确认游戏账号已登录，再勾选“仅确认本次执行”。");
        return new(true, ManualTestState.Ready, "可以进行手动实测：已确认本次执行，点击开始后由你亲自处理 UAC。");
    }
}
