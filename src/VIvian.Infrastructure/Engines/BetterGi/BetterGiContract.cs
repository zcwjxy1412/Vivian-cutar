using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>
/// Facts about BetterGI 0.66.0 that the adapter relies on, each with its evidence level. Everything here comes from
/// the pinned upstream source (tag 0.66.0, commit 0af68c2) and is level B at best: nothing was observed on the real
/// release. The two "verified" flags stay false until desktop evidence exists; while they are false the adapter never
/// launches BetterGI and never reports success.
/// </summary>
public static class BetterGiContract
{
    public const string UpstreamRepository = "https://github.com/babalae/better-genshin-impact";
    public const string PinnedTag = "0.66.0";
    public const string PinnedCommit = "0af68c28f6b0e714d5304625ce644736d508022e";
    public const string ProgramName = "BetterGI.exe";
    public const string ProcessName = "BetterGI";

    /// <summary>
    /// Launch, process ownership and stop behaviour have been observed on the real release. False: needs the Windows
    /// desktop validation listed in docs/m4/M4_BETTERGI_ADAPTER.md.
    /// </summary>
    public static bool LaunchContractVerified => false;

    /// <summary>The log markers below have been observed, for a run VIvian started, on the real release. False.</summary>
    public static bool CompletionSignalsVerified => false;

    // 0.66.0 can reach the original-resin path without entering its explicit-budget branch.
    public static bool DomainSpendLimitVerified => false;

    /// <summary>
    /// Exit code of a non-elevated BetterGI that restarted itself through the shell "runas" verb:
    /// <c>Environment.Exit(exitCode ?? 'r' + 'u' + 'n' + 'a' + 's')</c> = 553 (level B, Helpers/RuntimeHelper.cs).
    /// docs/research/M3_BETTERGI_ANALYSIS.md reports 559; the pinned source gives 553. The elevated copy is a new
    /// process outside VIvian's job, so this exit means ownership is lost.
    /// </summary>
    public const int ElevationRestartExitCode = 'r' + 'u' + 'n' + 'a' + 's';

    /// <summary>
    /// 0xFFFF: another instance already holds the single-instance event, or the Assets/GameTask folders are missing
    /// (level B, Helpers/RuntimeHelper.cs CheckSingleInstance / CheckIntegration).
    /// </summary>
    public const int SingleInstanceOrIntegrationExitCode = 0xFFFF;

    /// <summary>Folders BetterGI requires next to its program, else it exits with 0xFFFF (level B, CheckIntegration).</summary>
    public static IReadOnlyList<string> RequiredFolders { get; } = ["Assets", "GameTask"];

    /// <summary>Log folder relative to the installation (level B, App.xaml.cs: AppContext.BaseDirectory\log).</summary>
    public const string LogFolder = "log";

    /// <summary>
    /// Serilog daily rolling file: base name "better-genshin-impact.log" becomes "better-genshin-impact{yyyyMMdd}.log"
    /// for the local date (level B for the base name and rolling interval; the expanded name is Serilog behaviour).
    /// </summary>
    public static string LogFileName(DateOnly localDate) => $"better-genshin-impact{localDate:yyyyMMdd}.log";

    public static EvidenceLevel CliEvidence => EvidenceLevel.B;

    /// <summary>Source references shown in reports and PR descriptions.</summary>
    public static class Sources
    {
        public const string CommandLine = "better-genshin-impact@0af68c2 BetterGenshinImpact/Helpers/CommandLineOptions.cs";
        public const string Runtime = "better-genshin-impact@0af68c2 BetterGenshinImpact/Helpers/RuntimeHelper.cs";
        public const string Logging = "better-genshin-impact@0af68c2 BetterGenshinImpact/App.xaml.cs（Serilog 文件模板与 BgiInstance 字段）";
        public const string Instance = "better-genshin-impact@0af68c2 BetterGenshinImpact/Service/Instance/InstanceContext.cs";
        public const string OneDragonFlow = "better-genshin-impact@0af68c2 BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs";
        public const string TaskRunner = "better-genshin-impact@0af68c2 BetterGenshinImpact/GameTask/TaskRunner.cs";
    }
}
