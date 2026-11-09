using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Simulation;

internal static class DailyAcceptance
{
    public const string ProfileName = "VIvian 原神日常验收";
    private const string ProgramDigest = "73732F440BC8FF3B0272F2ADBBF72ACADCB952D45BF7F5A62CF0E3573DBD7D73";
    private const string ArchiveDigest = "28D4756E55F32637538BF84427F5F31AFD5B153BF996530C5975BD9C8C0B9343";
    private const string ProtocolRoot = @"Software\Classes\BetterGI";
    private const string ProtocolCommand = @"Software\Classes\BetterGI\shell\open\command";
    private const string OriginalProgram = @"C:\Program Files\BetterGI\BetterGI.exe";
    private const string RecoveryFile = "protocol-recovery.json";
    private static readonly string OriginalConfig = Path.Combine(Path.GetDirectoryName(OriginalProgram)!, "User", "config.json");
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static async Task<int> RunAsync(string stage, Guid? accountId = null)
    {
        string root, program;
        (stage, root, program) = await ValidateStageAsync(stage);
        var profileName = accountId is { } id ? PreparedAccountPlan.Verify(stage, id) : ProfileName;

        var settings = Path.Combine(root, "User", "config.json");
        string expectedGame;
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings)))
        {
            if (json.RootElement.GetProperty("commonConfig").GetProperty("runForVersion").GetString() != BetterGiContract.PinnedTag)
                throw new InvalidDataException("隔离安装的运行版本标记不符，避免上游启动时放开目录权限。");
            if (json.RootElement.GetProperty("selectedOneDragonFlowConfigName").GetString() != profileName)
                throw new InvalidDataException("隔离配置当前选中的任务档案不符。");
            var startup = json.RootElement.GetProperty("genshinStartConfig");
            if (startup.GetProperty("linkedStartEnabled").GetBoolean()
                || startup.GetProperty("startGameWithCmd").GetBoolean()
                || startup.GetProperty("autoEnterGameEnabled").GetBoolean()
                || startup.GetProperty("autoDisableGenshinHdrEnabled").GetBoolean())
                throw new InvalidDataException("隔离配置必须关闭游戏联动启动、自动登录和 HDR 修改。");
            expectedGame = startup.GetProperty("installPath").GetString() ?? "";
            if (!Path.IsPathFullyQualified(expectedGame))
                throw new InvalidDataException("隔离配置缺少原神客户端完整路径。");
        }

        using var lease = DesktopOperationLease.Acquire(stage);
        var plan = await BetterGiConfiguration.InspectOneDragonAsync(root, profileName);
        if (plan.CompletionAction != "关闭软件" || plan.EnabledTasks.Count == 0)
            throw new InvalidDataException("验收档案需要已启用的任务，结束后关闭 BetterGI。");
        if (accountId is null && !plan.EnabledTasks.SequenceEqual([BetterGiMarkers.DailyRewardTask]))
            throw new InvalidDataException("单次诊断只允许领取每日奖励。");
        if (accountId is not null && plan.EnabledTasks.Any(task => task is not ("领取每日奖励" or "合成树脂" or "自动秘境")))
            throw new InvalidDataException("隔离账号档案包含尚未登记的任务。");
        if (plan.EnabledTasks.Contains(BetterGiMarkers.DomainTask) && !BetterGiContract.DomainSpendLimitVerified)
            throw new InvalidDataException("当前 BetterGI 版本无法保证秘境树脂上限，验收任务未启动。");
        var probe = new BetterGiProcessProbe();
        if (probe.IsBusy(program)) throw new InvalidOperationException("已有 BetterGI 实例或 IPC 根，验收未开始。");

        if (FakeEngineAdapter.ForeignProcessIds(@"E:\auto agent game\VIvian\VIvian.exe").Count != 0)
            throw new InvalidOperationException("请先退出正式 furina，旧版不参与共同桌面锁。");
        if (accountId is not null)
            Console.WriteLine("本次使用你确认的当前账号和冻结任务档案，启用的树脂任务会消耗相应资源。");
        Console.WriteLine("10 秒内切回已经进入正确账号世界的原神。");
        await Task.Delay(TimeSpan.FromSeconds(10));
        VerifyActiveGame(expectedGame);
        using var scheme = Registry.CurrentUser.OpenSubKey(ProtocolRoot);
        if (!HasExpectedScheme(scheme))
            throw new InvalidOperationException("BetterGI 协议注册结构与固定版本不符。");
        using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolCommand, writable: true);
        var original = ReadRegistryString(protocol, string.Empty);
        if (original != Command(OriginalProgram))
            throw new InvalidOperationException("当前 BetterGI 协议目标与预期安装不符，拒绝修改。");

        var runId = Guid.NewGuid();
        var reportPath = ReportPath(stage, runId);
        var recovery = Path.Combine(stage, RecoveryFile);
        RecoveryJournal journal;
        using (var config = OpenVerifiedFile(OriginalConfig))
            journal = new(runId, original, Command(program), FileRevision(config));

        var request = BetterGiArguments.OneDragon(profileName).Request!;
        var adapter = new BetterGiAdapter(() => root, request);
        var context = new RunContext(runId, runId, GameId.Genshin, DateTimeOffset.UtcNow);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var userCancelled = false;
        ConsoleCancelEventHandler interrupt = (_, args) =>
        {
            args.Cancel = true;
            userCancelled = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += interrupt;
        RunVerdict? verdict = null;
        BetterGiRunObservation? observation = null;
        var cleanup = CleanupState.Unconfirmed;
        var restored = false;
        string? startError = null;
        try
        {
            VerifyActiveGame(expectedGame);
            if (!HasExpectedScheme(scheme) || ReadRegistryString(protocol, string.Empty) != journal.Original)
                throw new InvalidOperationException("BetterGI 协议注册在启动前发生变化，拒绝启动。");
            WriteEvidence(recovery, journal);
            lease.MarkDirty();
            await using var run = await adapter.LaunchAsync(context, new WindowsProcessSupervisor(), stop.Token);
            Console.WriteLine("原神短任务已启动。按 Ctrl+C 只终止本次受监管进程。");
            try
            {
                verdict = await run.WaitForVerdictAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await run.RequestStopAsync(StopMode.Emergency, stopLimit.Token);
            }
            using var reconcileLimit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cleanup = await run.ReconcileAsync(reconcileLimit.Token);
            verdict ??= await run.WaitForVerdictAsync(reconcileLimit.Token);
            observation = ((BetterGiRun)run).Observe();
        }
        catch (EngineStartException ex)
        {
            startError = ex.ReasonCode;
            cleanup = ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed;
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
            var report = new AcceptanceReport(runId, verdict?.Outcome, verdict?.ReasonCode, verdict?.Evidence,
                observation, userCancelled, stop.IsCancellationRequested && !userCancelled, cleanup,
                ProtocolRestored: false, OriginalConfigUnchanged: false, startError);
            WriteEvidence(reportPath, report);
            Console.WriteLine("验收报告 " + reportPath);
            restored = cleanup is (CleanupState.Confirmed or CleanupState.NotRequired)
                && TryRestore(lease, program, journal, reportPath, report);
            if (restored) File.Delete(recovery);
        }
        return restored && !stop.IsCancellationRequested
            && observation is { OwnedPrimary: true, PlannedTasksVerified: true, FlowEnded: true, ForeignActivitySeen: false,
                AdverseSignal: false, ExitCode: 0 } ? 0 : 1;
    }

    public static async Task<int> RecoverAsync(string stage)
    {
        string program;
        (stage, _, program) = await ValidateStageAsync(stage);
        using var lease = DesktopOperationLease.Acquire(stage, recovery: true);
        if (!lease.Recovering)
            throw new InvalidOperationException("该验收目录没有待恢复的桌面锁标记。");

        var recovery = Path.Combine(stage, RecoveryFile);
        using (var journalFile = OpenVerifiedFile(recovery))
        {
            var journal = ReadEvidence<RecoveryJournal>(journalFile);
            if (!JournalMatches(journal, program))
                throw new InvalidDataException("待恢复记录与隔离安装、原协议或原配置摘要不符。");
            var reportPath = ReportPath(stage, journal.RunId);
            AcceptanceReport report;
            using (var reportFile = OpenVerifiedFile(reportPath))
                report = ReadEvidence<AcceptanceReport>(reportFile);
            if (!HasRecoveryProof(report, journal.RunId))
                throw new InvalidDataException("验收报告缺少本次运行已清理的证据，保留待恢复状态。");
            if (!TryRestore(lease, program, journal, reportPath, report))
                throw new InvalidOperationException("仍有 BetterGI 活动，或原配置、协议注册存在冲突，保留待恢复状态。");
        }
        File.Delete(recovery);
        Console.WriteLine("已核对协议和原配置，并解除验收桌面锁。");
        return 0;
    }

    private static bool HasRecoveryProof(AcceptanceReport report, Guid runId)
    {
        if (report.RunId != runId || report.UserCancelled || report.TimedOut) return false;
        return report.Cleanup switch
        {
            CleanupState.NotRequired => report.StartError is (ReasonCodes.NotConfigured or ReasonCodes.StartFailed
                or ReasonCodes.PreflightError or ReasonCodes.ForeignProcess)
                && report.Outcome is null && report.ReasonCode is null && report.Evidence is null && report.Observation is null,
            CleanupState.Confirmed => report.StartError is null
                && report.Outcome is (RunOutcome.Success or RunOutcome.Indeterminate)
                && report.ReasonCode is (ReasonCodes.Completed or ReasonCodes.RealAdapterUnverified or ReasonCodes.NoCompletionEvidence)
                && report.Observation is { ProcessId: > 0, OwnedPrimary: true, ForeignActivitySeen: false, AdverseSignal: false, ExitCode: 0 } observed
                && observed.ProcessStartedAt > DateTimeOffset.UnixEpoch
                && report.Evidence is { Count: > 0 }
                && report.Evidence.All(e => e is not null && e.RunId == runId && e.Kind != EvidenceKind.CancelReported),
            _ => false,
        };
    }

    private static bool JournalMatches(RecoveryJournal journal, string program) =>
        journal.RunId != Guid.Empty && journal.Original == Command(OriginalProgram) && journal.Expected == Command(program)
        && journal.OriginalConfigRevision is { Length: 64 } revision && revision.All(Uri.IsHexDigit);

    private static bool TryRestore(DesktopOperationLease lease, string program, RecoveryJournal journal,
        string reportPath, AcceptanceReport report)
    {
        using var config = OpenVerifiedFile(OriginalConfig);
        if (FileRevision(config) != journal.OriginalConfigRevision || IsBetterGiBusy(program)) return false;

        using var scheme = Registry.CurrentUser.OpenSubKey(ProtocolRoot);
        using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolCommand);
        if (!HasExpectedScheme(scheme)) return false;
        var current = ReadRegistryString(protocol, string.Empty);
        if (current == journal.Expected)
        {
            using var writable = Registry.CurrentUser.OpenSubKey(ProtocolCommand, writable: true);
            if (writable is null || IsBetterGiBusy(program) || !HasExpectedScheme(scheme)
                || ReadRegistryString(writable, string.Empty) != journal.Expected) return false;
            writable.SetValue(string.Empty, journal.Original, RegistryValueKind.String);
            writable.Flush();
        }
        else if (current != journal.Original) return false;

        if (!HasExpectedScheme(scheme) || ReadRegistryString(protocol, string.Empty) != journal.Original) return false;
        var update = reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteEvidence(update, report with { ProtocolRestored = true, OriginalConfigUnchanged = true });
            File.Move(update, reportPath, overwrite: true);
        }
        finally
        {
            File.Delete(update);
        }
        if (IsBetterGiBusy(program) || !HasExpectedScheme(scheme)
            || ReadRegistryString(protocol, string.Empty) != journal.Original) return false;
        lease.ConfirmClean();
        return true;
    }

    private static bool IsBetterGiBusy(string program)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program));
        foreach (var process in processes) process.Dispose();
        return processes.Length != 0 || new BetterGiProcessProbe().IsBusy(program);
    }

    private static string? ReadRegistryString(RegistryKey? key, string name) =>
        key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
            && key.GetValueKind(name) == RegistryValueKind.String ? value : null;

    private static bool HasExpectedScheme(RegistryKey? scheme) =>
        ReadRegistryString(scheme, string.Empty) == "BetterGI" && ReadRegistryString(scheme, "URL Protocol") == "";

    private static string FileRevision(FileStream file)
    {
        if (file.Length > BetterGiConfiguration.MaxConfigBytes)
            throw new InvalidDataException("原 BetterGI 配置超过大小限制，无法确认摘要。");
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string Command(string path) => $"\"{path}\" \"%1\"";

    private static string ReportPath(string stage, Guid runId) => Path.Combine(stage, runId.ToString("N") + ".json");

    private static FileStream OpenVerifiedFile(string path)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
            throw new InvalidDataException("验收文件路径含链接。");
        var file = ReadOnlyFileAccess.OpenForStableRead(path);
        if (ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path) && ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) == 1)
            return file;
        file.Dispose();
        throw new InvalidDataException("无法确认验收文件的实际路径与链接数。");
    }

    private static T ReadEvidence<T>(FileStream file) where T : class
    {
        if (!IsAdministratorOnly(file.GetAccessControl()) || file.Length > 1024 * 1024)
            throw new InvalidDataException("验收证据的权限或文件大小不符。");
        return JsonSerializer.Deserialize<T>(file, EvidenceJson) ?? throw new InvalidDataException("验收证据为空。");
    }

    private static void WriteEvidence<T>(string path, T evidence)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileSecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(system, FileSystemRights.FullControl, AccessControlType.Allow));
        using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.None, security);
        JsonSerializer.Serialize(file, evidence, EvidenceJson);
        file.Flush(flushToDisk: true);
    }

    private sealed record RecoveryJournal(Guid RunId, string Original, string Expected, string OriginalConfigRevision);

    private sealed record AcceptanceReport(Guid RunId, RunOutcome? Outcome, string? ReasonCode, IReadOnlyList<Evidence>? Evidence,
        BetterGiRunObservation? Observation, bool UserCancelled, bool TimedOut, CleanupState Cleanup,
        bool ProtocolRestored, bool OriginalConfigUnchanged, string? StartError);

    private static async Task<(string Stage, string Root, string Program)> ValidateStageAsync(string stage)
    {
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请在同一 Windows 用户的管理员终端中运行原神验收或恢复。");

        stage = Path.GetFullPath(stage);
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VIvian M6 BetterGI Acceptance");
        if (!string.Equals(Path.GetDirectoryName(stage), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw new InvalidOperationException("验收只允许使用准备脚本生成的独立安装。");
        EnsureProtectedStage(parent, stage);
        var root = Path.Combine(stage, "BetterGI");
        if (ReadOnlyFileAccess.FindReparsePoint(root) is not null
            || !IsAdministratorOnly(new DirectoryInfo(root).GetAccessControl()))
            throw new InvalidOperationException("隔离 BetterGI 安装的权限与准备阶段不一致。");
        var program = Path.Combine(root, BetterGiContract.ProgramName);
        await using var executable = OpenVerifiedFile(program);
        if (Convert.ToHexString(await SHA256.HashDataAsync(executable)) != ProgramDigest)
            throw new InvalidDataException("隔离安装的 BetterGI.exe 摘要不匹配。");
        return (stage, root, program);
    }

    private static void EnsureProtectedStage(string parent, string stage)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(stage) is not null)
            throw new InvalidOperationException("独立验收目录含链接。");
        var parentAcl = new DirectoryInfo(parent).GetAccessControl();
        if (!parentAcl.AreAccessRulesProtected || !IsAdministratorOnly(parentAcl)
            || !IsAdministratorOnly(new DirectoryInfo(stage).GetAccessControl()))
            throw new InvalidOperationException("独立验收目录没有管理员专用权限。");
        var verification = Path.Combine(stage, "verified-archive.txt");
        if (!File.Exists(verification))
            throw new InvalidOperationException("独立验收目录缺少固定官方包的核对标记。");
        using var file = OpenVerifiedFile(verification);
        using var reader = new StreamReader(file);
        if (file.Length > 128 || reader.ReadToEnd().Trim() != ArchiveDigest)
            throw new InvalidOperationException("独立验收目录缺少固定官方包的核对标记。");
    }

    private static bool IsAdministratorOnly(FileSystemSecurity acl)
    {
        var allows = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        return acl.GetOwner(typeof(SecurityIdentifier))?.Value is ("S-1-5-32-544" or "S-1-5-18")
            && allows.Length != 0
            && allows.All(rule => rule.IdentityReference.Value is ("S-1-5-32-544" or "S-1-5-18"));
    }

    private static void VerifyActiveGame(string expectedGame)
    {
        using var self = Process.GetCurrentProcess();
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var pid);
        if (pid == 0) throw new InvalidOperationException("请先打开原神并进入已登录世界。");

        // The upstream finds an existing client by name and session, not by the configured executable path.
        string[] candidates = ["YuanShen", "GenshinImpact", "Genshin Impact Cloud Game", "Genshin Impact Cloud"];
        var processes = candidates.SelectMany(name => Process.GetProcessesByName(name)).ToArray();
        try
        {
            var session = processes.Where(p => p.SessionId == self.SessionId).ToArray();
            if (session.Length != 1 || session[0].Id != (int)pid
                || !string.Equals(ReadGameExecutable(session[0].Id), expectedGame, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("验收需要同一会话仅有一个指定的原神客户端，且它在前台。");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static string ReadGameExecutable(int processId)
    {
        const uint queryLimitedInformation = 0x1000;
        using var handle = NativeMethods.OpenProcess(queryLimitedInformation, false, processId);
        var path = new StringBuilder(32768);
        var length = (uint)path.Capacity;
        if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, path, ref length))
            throw new InvalidOperationException("无法核对原神客户端的程序路径。");
        return path.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
