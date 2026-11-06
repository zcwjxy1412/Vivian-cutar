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
/// A task identifier taken from upstream source or documentation. It is a candidate only: VIvian never sends it to an
/// engine until the exact release has been observed to accept it and its completion signals are verified.
/// </summary>
public sealed record EngineTaskDefinition(
    string Id, string Title, CapabilityStatus Status, EvidenceLevel Evidence, string Source, string Note);

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
/// The single place that decides whether a real engine may be launched. In this build the answer is always no:
/// launching requires desktop evidence (process ownership across UAC, completion signals, side effects) that has
/// not been collected. The scheduler blocks non-simulation adapters independently of this policy.
/// </summary>
public static class RealExecutionPolicy
{
    /// <summary>Only the separately stamped, one-task Windows test build permits explicit screen_test admission.</summary>
#if M4_SCREEN_TEST
    public static bool BuildPermitsRealExecution => true;
#else
    public static bool BuildPermitsRealExecution => false;
#endif
    public static bool AllowsTask(string task) => BuildPermitsRealExecution && task == "screen_test";

    public static ExecutionDecision Evaluate(EngineReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        // Even a future build that permits execution needs verified identity plus verified launch and completion
        // evidence for this exact release; until then the answer stays no.
        return new(false, Execution.ReasonCodes.RealAdapterUnverified,
            BuildPermitsRealExecution
                ? "此单次测试包只允许从星铁实测页确认 screen_test，且必须通过即时状态检查。"
                : "此开发版不启动任何真实引擎：进程归属、UAC 边界与完成证据尚待 Windows 桌面实测。");
    }
}
