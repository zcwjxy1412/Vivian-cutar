namespace VIvian.Infrastructure.Engines.OneDragon;

/// <summary>
/// Facts about ZenlessZoneZero-OneDragon v2.6.0 (commit 5f2e473) that the adapter relies on. All are level B (pinned
/// source) or C (upstream documentation); nothing was observed on the real release. While the two "verified" flags are
/// false the adapter never launches OneDragon and never reports success.
/// </summary>
public static class OneDragonContract
{
    public const string UpstreamRepository = "https://github.com/OneDragon-Anything/ZenlessZoneZero-OneDragon";
    // SHA-256 of the launcher from the verified WithRuntime-Full release ZIP.
    public const string LauncherSha256 = "d4a8e2780eacc6754a88810d1d6b2fccdf4dd60a1e00725a12b5ebe8589158c1";
    public const string PinnedTag = "v2.6.0";
    public const string PinnedCommit = "5f2e473bbc5b100ae2ea482dd25be0277bbca87c";

    /// <summary>The integrated-runtime launcher: the only managed entry (the plain launcher spawns a separate Python).</summary>
    public const string EntryProgram = "OneDragon-RuntimeLauncher.exe";
    public const string RawLauncherProgram = "OneDragon-Launcher.exe";

    /// <summary>Launch, ownership and stop behaviour observed on the real release. False.</summary>
    public static bool LaunchContractVerified => false;

    /// <summary>The output markers below observed, for a run VIvian started, on the real release. False.</summary>
    public static bool CompletionSignalsVerified => false;

    /// <summary>
    /// Folders next to the launcher. Without <c>src</c> the launcher shows a modal error box and exits 1
    /// (level B, src/zzz_od/win_exe/runtime_launcher.py); <c>.runtime</c> holds the embedded Python (level B, docs/develop/one_dragon/runtime_launcher.md).
    /// </summary>
    public static IReadOnlyList<string> RequiredFolders { get; } = ["src", ".runtime"];

    /// <summary>
    /// Standard-stream encoding requested from the embedded Python, so the supervisor's UTF-8 reader decodes the log
    /// lines. Whether the frozen runtime honours it is unverified; if it does not, markers simply never match and the
    /// verdict stays Indeterminate. PYTHONUTF8 is deliberately not set: it would change how OneDragon reads and writes
    /// its own files.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>
    {
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUNBUFFERED"] = "1",
    };

    public static class Sources
    {
        public const string LauncherBase = "ZenlessZoneZero-OneDragon@5f2e473 src/one_dragon/launcher/launcher_base.py（-o/-c/-i/-s/--hibernate/--sleep）";
        public const string ExeLauncher = "ZenlessZoneZero-OneDragon@5f2e473 src/one_dragon/launcher/exe_launcher.py（非管理员时 pyuac.runAsAdmin(wait=False) 后 sys.exit(0)）";
        public const string RuntimeLauncher = "ZenlessZoneZero-OneDragon@5f2e473 src/one_dragon/launcher/runtime_launcher.py（_sync_code、致命错误弹窗）";
        public const string ApplicationLauncher = "ZenlessZoneZero-OneDragon@5f2e473 src/one_dragon/launcher/application_launcher.py（实例解析、run_application）";
        public const string Operation = "ZenlessZoneZero-OneDragon@5f2e473 src/one_dragon/base/operation/operation.py（指令[ … ] 执行成功/失败）";
        public const string Logging = "ZenlessZoneZero-OneDragon@5f2e473 src/one_dragon/utils/log_utils.py（控制台格式，无 PID 字段）";
        public const string Spec = "ZenlessZoneZero-OneDragon@5f2e473 deploy/OneDragon-RuntimeLauncher.spec（console=True）";
        public const string PathRule = "one-dragon.com 快速开始文档（级别 C；固定源码中未发现该检查）";
    }
}
