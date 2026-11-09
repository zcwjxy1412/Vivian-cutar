using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
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
    private static readonly string OriginalConfig = Path.Combine(Path.GetDirectoryName(OriginalProgram)!, "User", "config.json");



    public static async Task<int> RunAsync(string stage)
    {
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请在同一 Windows 用户的管理员终端中运行原神验收。");


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
        if (ReadOnlyFileAccess.FindReparsePoint(program) is not null)
            throw new InvalidDataException("隔离安装程序路径含链接。");
        await using (var executable = ReadOnlyFileAccess.OpenForStableRead(program))
        {
            if (!ReadOnlyFileAccess.HandlePathMatches(executable.SafeFileHandle, program)
                || ReadOnlyFileAccess.LinkCount(executable.SafeFileHandle) != 1)
                throw new InvalidDataException("无法确认 BetterGI 程序文件属于隔离目录。");
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(executable));
            if (sha != ProgramDigest) throw new InvalidDataException("隔离安装的 BetterGI.exe 摘要不匹配。");
        }

        var settings = Path.Combine(root, "User", "config.json");
        string expectedGame;
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(settings)))
        {
            if (json.RootElement.GetProperty("commonConfig").GetProperty("runForVersion").GetString() != BetterGiContract.PinnedTag)
                throw new InvalidDataException("隔离安装的运行版本标记不符，避免上游启动时放开目录权限。");
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
        var plan = await BetterGiConfiguration.InspectOneDragonAsync(root, ProfileName);
        if (!plan.EnabledTasks.SequenceEqual([BetterGiMarkers.DailyRewardTask]) || plan.CompletionAction != "关闭软件")
            throw new InvalidDataException("验收配置必须只启用领取每日奖励，并在完成后关闭软件。");
        var probe = new BetterGiProcessProbe();
        if (probe.IsBusy(program)) throw new InvalidOperationException("已有 BetterGI 实例或 IPC 根，验收未开始。");

        if (FakeEngineAdapter.ForeignProcessIds(@"E:\auto agent game\VIvian\VIvian.exe").Count != 0)
            throw new InvalidOperationException("请先退出正式 furina，旧版不参与共同桌面锁。");
        Console.WriteLine("10 秒内切回已经进入正确账号世界的原神。");
        await Task.Delay(TimeSpan.FromSeconds(10));
        VerifyActiveGame(expectedGame);
        using var scheme = Registry.CurrentUser.OpenSubKey(ProtocolRoot);
        if (!Equals(scheme?.GetValue(string.Empty), "BetterGI") || !Equals(scheme.GetValue("URL Protocol"), ""))
            throw new InvalidOperationException("BetterGI 协议注册结构与固定版本不符。");
        var configRevision = FileRevision(OriginalConfig);
        using var protocol = Registry.CurrentUser.OpenSubKey(ProtocolCommand, writable: true)
            ?? throw new InvalidOperationException("现有 BetterGI 协议注册不可用。");
        var original = protocol.GetValue(string.Empty) as string;
        if (!string.Equals(original, Command(OriginalProgram), StringComparison.OrdinalIgnoreCase)
            || protocol.GetValueKind(string.Empty) != RegistryValueKind.String)
            throw new InvalidOperationException("当前 BetterGI 协议目标与预期安装不符，拒绝修改。");

        var runId = Guid.NewGuid();
        var report = Path.Combine(stage, runId.ToString("N") + ".json");
        var recovery = Path.Combine(stage, "protocol-recovery.json");


        var request = BetterGiArguments.OneDragon(ProfileName).Request!;
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
        var originalConfigUnchanged = false;
        string? startError = null;
        try
        {
            VerifyActiveGame(expectedGame);
            using (var file = new FileStream(recovery, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new { RunId = runId, Original = original, Expected = Command(program) });
                file.Flush(flushToDisk: true);
            }
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
            if (cleanup is (CleanupState.Confirmed or CleanupState.NotRequired) && !probe.IsBusy(program))
            {
                var current = protocol.GetValue(string.Empty) as string;
                if (current == Command(program))
                    protocol.SetValue(string.Empty, original!, RegistryValueKind.String);
                restored = Equals(protocol.GetValue(string.Empty), original)
                    && Equals(scheme.GetValue(string.Empty), "BetterGI") && Equals(scheme.GetValue("URL Protocol"), "");
                originalConfigUnchanged = FileRevision(OriginalConfig) == configRevision;
            }

            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                RunId = runId, verdict?.Outcome, verdict?.ReasonCode, verdict?.Evidence, Observation = observation,
                UserCancelled = userCancelled, TimedOut = stop.IsCancellationRequested && !userCancelled,
                Cleanup = cleanup, ProtocolRestored = restored, OriginalConfigUnchanged = originalConfigUnchanged, StartError = startError,
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
            if (restored && originalConfigUnchanged)
            {
                lease.ConfirmClean();
                File.Delete(recovery);
            }
            Console.WriteLine("验收报告 " + report);
        }
        return restored && originalConfigUnchanged && !stop.IsCancellationRequested
            && observation is { OwnedPrimary: true, DailyRewardVerified: true, FlowEnded: true, ForeignActivitySeen: false,
                AdverseSignal: false, ExitCode: 0 } ? 0 : 1;
    }

    private static string FileRevision(string path)
    {
        using var file = ReadOnlyFileAccess.OpenForStableRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string Command(string path) => $"\"{path}\" \"%1\"";

    private static void EnsureProtectedStage(string parent, string stage)
    {
        if (ReadOnlyFileAccess.FindReparsePoint(stage) is not null)
            throw new InvalidOperationException("独立验收目录含链接。");
        var parentAcl = new DirectoryInfo(parent).GetAccessControl();
        if (!parentAcl.AreAccessRulesProtected || !IsAdministratorOnly(parentAcl)
            || !IsAdministratorOnly(new DirectoryInfo(stage).GetAccessControl()))
            throw new InvalidOperationException("独立验收目录没有管理员专用权限。");
        var verification = Path.Combine(stage, "verified-archive.txt");
        if (!File.Exists(verification) || File.ReadAllText(verification).Trim() != ArchiveDigest)
            throw new InvalidOperationException("独立验收目录缺少固定官方包的核对标记。");
    }

    private static bool IsAdministratorOnly(DirectorySecurity acl)
    {
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return acl.GetOwner(typeof(SecurityIdentifier))?.Value is ("S-1-5-32-544" or "S-1-5-18")
            && rules.Cast<FileSystemAccessRule>().All(rule => rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference.Value is ("S-1-5-32-544" or "S-1-5-18"));
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
                || !string.Equals(session[0].MainModule?.FileName, expectedGame, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("验收需要同一会话仅有一个指定的原神客户端，且它在前台。");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
