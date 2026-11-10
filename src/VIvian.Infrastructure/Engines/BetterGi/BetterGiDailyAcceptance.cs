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
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Processes;
using VIvian.Infrastructure.Simulation;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed record BetterGiDailyAcceptanceResult(Guid RunId, RunVerdict? Verdict, CleanupState Cleanup,
    bool Restored, bool EvidenceObserved, bool Cancelled, bool TimedOut, string ReportPath, string? StartError)
{
    public bool DailyRewardClaimUnconfirmed { get; init; }
    public RunOutcome Outcome => Restored && Cleanup == CleanupState.Confirmed && !Cancelled && !TimedOut
        ? Verdict?.Outcome ?? RunOutcome.Indeterminate : RunOutcome.Indeterminate;
    public bool DiagnosticPassed => Restored && Cleanup == CleanupState.Confirmed && EvidenceObserved && !Cancelled && !TimedOut;
    internal bool ScopedCompletionConfirmed => DiagnosticPassed && (Verdict?.Outcome == RunOutcome.Success ||
        Verdict is { Outcome: RunOutcome.Indeterminate, ReasonCode: ReasonCodes.RealAdapterUnverified or ReasonCodes.NoCompletionEvidence });
}

internal static class BetterGiDailyAcceptance
{
    public const string ProfileName = "VIvian 原神日常验收";
    private const string ProgramDigest = "73732F440BC8FF3B0272F2ADBBF72ACADCB952D45BF7F5A62CF0E3573DBD7D73";
    private const string ArchiveDigest = "28D4756E55F32637538BF84427F5F31AFD5B153BF996530C5975BD9C8C0B9343";
    private const string ProtocolRoot = @"Software\Classes\BetterGI";
    private const string ProtocolCommand = @"Software\Classes\BetterGI\shell\open\command";
    private const string OriginalProgram = @"C:\Program Files\BetterGI\BetterGI.exe";
    private const string RecoveryFile = "protocol-recovery.json";
    private static string StageParent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "VIvian M6 BetterGI Acceptance");
    private static readonly string OriginalConfig = Path.Combine(Path.GetDirectoryName(OriginalProgram)!, "User", "config.json");
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Operation = new(1, 1);
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    internal static async Task<BetterGiDailyAcceptanceResult> RunAsync(string stage, Guid accountId, BetterGiPlanFiles expected,
        DesktopOperationLease lease, Func<CancellationToken, Task<GenshinVerifiedWorld>> recheckWorld, CancellationToken ct,
        Func<GenshinVerifiedWorld, CancellationToken, Task<(int Original, int Condensed)>>? readInventory = null)
    {
        ArgumentNullException.ThrowIfNull(recheckWorld);
        var owner = lease.VerifyCommonOwner();
        if (lease.Recovering) throw new InvalidOperationException("待恢复的桌面锁不能启动新任务。");
        var validated = await ValidateStageAsync(stage, ct).ConfigureAwait(false);
        var expectedGame = ValidateDailyFiles(validated.Root, accountId, expected, allowStockCraft: readInventory is not null);
        return await RunCoreAsync(validated, expected.ProfileName, expectedGame, lease, owner, accountId,
            allowAccountTasks: false, async token => await recheckWorld(token).ConfigureAwait(false),
            revalidate: () => ValidateDailyFiles(validated.Root, accountId, expected, allowStockCraft: readInventory is not null),
            status: null, ct, readInventory).ConfigureAwait(false);
    }

    internal static async Task<BetterGiDailyAcceptanceResult> RunLegacyAsync(string stage,
        Func<string, string>? verifyAccountPlan, Action<string> status, CancellationToken ct)
    {
        var validated = await ValidateStageAsync(stage, ct).ConfigureAwait(false);
        var profileName = verifyAccountPlan?.Invoke(validated.Stage) ?? ProfileName;
        using var lease = DesktopOperationLease.Acquire(validated.Stage);
        var expectedGame = ReadGamePath(Path.Combine(validated.Root, "User", "config.json"), profileName);
        return await RunCoreAsync(validated, profileName, expectedGame, lease, lease.VerifyCommonOwner(), accountId: null,
            allowAccountTasks: verifyAccountPlan is not null,
            async token =>
            {
                if (verifyAccountPlan is not null)
                    status("本次使用你确认的当前账号和冻结任务档案，启用的树脂任务会消耗相应资源。");
                status("10 秒内切回已经进入正确账号世界的原神。");
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                return null;
            },
            revalidate: () => ReadGamePath(Path.Combine(validated.Root, "User", "config.json"), profileName),
            status, ct, readInventory: null).ConfigureAwait(false);
    }

    private static async Task<BetterGiDailyAcceptanceResult> RunCoreAsync(
        (string Stage, string Root, string Program) validated, string profileName, string expectedGame,
        DesktopOperationLease lease, string owner, Guid? accountId, bool allowAccountTasks,
        Func<CancellationToken, Task<GenshinVerifiedWorld?>> recheckWorld, Func<string> revalidate,
        Action<string>? status, CancellationToken ct,
        Func<GenshinVerifiedWorld, CancellationToken, Task<(int Original, int Condensed)>>? readInventory)
    {
        if (!await Operation.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("已有原神日常验收正在运行。");
        try
        {
            return await RunLockedAsync(validated, profileName, expectedGame, lease, owner, accountId,
                allowAccountTasks, recheckWorld, revalidate, status, ct, readInventory).ConfigureAwait(false);
        }
        finally { Operation.Release(); }
    }

    private static async Task<BetterGiDailyAcceptanceResult> RunLockedAsync(
        (string Stage, string Root, string Program) validated, string profileName, string expectedGame,
        DesktopOperationLease lease, string owner, Guid? accountId, bool allowAccountTasks,
        Func<CancellationToken, Task<GenshinVerifiedWorld?>> recheckWorld, Func<string> revalidate,
        Action<string>? status, CancellationToken ct,
        Func<GenshinVerifiedWorld, CancellationToken, Task<(int Original, int Condensed)>>? readInventory)
    {
        var (stage, root, program) = validated;
        var recovery = Path.Combine(stage, RecoveryFile);
        if (File.Exists(recovery)) throw new InvalidOperationException("隔离档案尚有待恢复的 BetterGI 验收记录。");
        var plan = await BetterGiConfiguration.InspectOneDragonAsync(root, profileName, ct).ConfigureAwait(false);
        if (plan.CompletionAction != "关闭软件" || plan.EnabledTasks.Count == 0)
            throw new InvalidDataException("验收档案需要已启用的任务，结束后关闭 BetterGI。");
        var verifyCraft = readInventory is not null && plan.EnabledTasks.SequenceEqual([BetterGiMarkers.CraftResinTask, BetterGiMarkers.DailyRewardTask]);
        if (!allowAccountTasks && !verifyCraft && !plan.EnabledTasks.SequenceEqual([BetterGiMarkers.DailyRewardTask]))
            throw new InvalidDataException("需要独立库存核验才能运行合成任务。");
        if (allowAccountTasks && plan.EnabledTasks.Any(task => task is not ("领取每日奖励" or "合成树脂" or "自动秘境")))
            throw new InvalidDataException("隔离账号档案包含尚未登记的任务。");
        if (plan.EnabledTasks.Contains(BetterGiMarkers.DomainTask) && !BetterGiContract.DomainSpendLimitVerified)
            throw new InvalidDataException("当前 BetterGI 版本无法保证秘境树脂上限，验收任务未启动。");
        if (IsBetterGiBusy(program)) throw new InvalidOperationException("已有 BetterGI 实例或 IPC 根，验收未开始。");
        RequireLegacyLauncherClosed();

        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var scheme = registry.OpenSubKey(ProtocolRoot);
        if (!HasExpectedScheme(scheme))
            throw new InvalidOperationException("BetterGI 协议注册结构与固定版本不符。");
        using var protocol = registry.OpenSubKey(ProtocolCommand, writable: true);
        var original = ReadRegistryString(protocol, string.Empty);
        if (original != Command(OriginalProgram))
            throw new InvalidOperationException("当前 BetterGI 协议目标与预期安装不符，拒绝修改。");

        var runId = Guid.NewGuid();
        var reportPath = ReportPath(stage, runId);
        RecoveryJournal journal;
        using (var config = OpenVerifiedFile(OriginalConfig))
            journal = new(runId, original, Command(program), FileRevision(config), owner);

        using var recheck = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recheck.CancelAfter(verifyCraft ? GenshinResinReadiness.VerificationTimeout : TimeSpan.FromSeconds(30));
        var world = await recheckWorld(recheck.Token).WaitAsync(recheck.Token).ConfigureAwait(false);
        if (accountId is { } id && (world is null || world.AccountId != id))
            throw new InvalidOperationException("无法重新确认本次账号已进入原神世界，验收未启动。");
        if (!string.Equals(revalidate(), expectedGame, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原神客户端配置在启动前发生变化。");
        VerifyActiveGame(expectedGame, accountId, world);
        var resinSession = world is null ? default : new BetterGiResinSession(runId, world.ProcessId, world.ProcessStartedAt);
        BetterGiResinInventory? before = null;
        if (verifyCraft)
        {
            var startWorld = world ?? throw new InvalidOperationException("合成任务缺少原神入场身份。");
            var stock = await readInventory!(startWorld, recheck.Token).ConfigureAwait(false);
            before = new(new(resinSession, 1, TimeProvider.System.GetTimestamp()), stock.Original, stock.Condensed);
            world = await ConfirmSameWorldAsync(startWorld, recheckWorld, recheck.Token).ConfigureAwait(false);
        }

        var request = BetterGiArguments.OneDragon(profileName).Request!;
        var adapter = new BetterGiAdapter(() => root, request);
        var context = new RunContext(runId, runId, GameId.Genshin, DateTimeOffset.UtcNow);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(RunTimeout);
        RunVerdict? verdict = null;
        BetterGiRunObservation? observation = null;
        var cleanup = CleanupState.Unconfirmed;
        var restored = false;
        string? startError = null;
        if (!HasExpectedScheme(scheme) || ReadRegistryString(protocol, string.Empty) != journal.Original
            || lease.VerifyCommonOwner() != owner)
            throw new InvalidOperationException("BetterGI 协议注册或桌面锁在启动前发生变化，拒绝启动。");
        VerifyActiveGame(expectedGame, accountId, world);
        ct.ThrowIfCancellationRequested();
        WriteEvidence(recovery, journal);
        lease.MarkDirty();
        try
        {
            await using var run = await adapter.LaunchAsync(context, new WindowsProcessSupervisor(), stop.Token).ConfigureAwait(false);
            status?.Invoke("原神短任务已启动。按 Ctrl+C 只终止本次受监管进程。");
            try
            {
                verdict = await run.WaitForVerdictAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await run.RequestStopAsync(StopMode.Emergency, stopLimit.Token).ConfigureAwait(false);
            }
            using var reconcileLimit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            cleanup = await run.ReconcileAsync(reconcileLimit.Token).ConfigureAwait(false);
            verdict ??= await run.WaitForVerdictAsync(reconcileLimit.Token).ConfigureAwait(false);
            observation = ((BetterGiRun)run).Observe();
            if (before is { } startingStock && cleanup == CleanupState.Confirmed && !stop.IsCancellationRequested &&
                observation is { OwnedPrimary: true, FlowEnded: true, DailyRewardVerified: true, ForeignActivitySeen: false,
                    AdverseSignal: false, ExitCode: 0, CraftAction: BetterGiCraftAction.Attempt or BetterGiCraftAction.Skipped })
            {
                using var inventoryLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                inventoryLimit.CancelAfter(GenshinResinReadiness.VerificationTimeout);
                try
                {
                    var same = await ConfirmSameWorldAsync(world!, recheckWorld, inventoryLimit.Token).ConfigureAwait(false);
                    var stock = await readInventory!(same, inventoryLimit.Token).ConfigureAwait(false);
                    var after = new BetterGiResinInventory(new(resinSession, 3, TimeProvider.System.GetTimestamp()), stock.Original, stock.Condensed);
                    var action = new BetterGiResinEvent(resinSession, 2, observation.CraftActionTimestamp);
                    var crafted = BetterGiResinSafety.ConfirmStockCraft(resinSession, startingStock, action, after, observation.CraftAction);
                    if (crafted is not null)
                    {
                        observation = observation with { CraftedCount = crafted, PlannedTasksVerified = true };
                        verdict = WithCraftEvidence(verdict, runId, crafted.Value);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
                {
                    // The engine is already reconciled. Keep the uncertain result and still restore its protocol.
                }
            }
        }
        catch (EngineStartException ex)
        {
            startError = ex.ReasonCode;
            cleanup = ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed;
        }
        catch (OperationCanceledException)
        {
            // A cancellation outside the bounded stop/reconcile path does not prove the process exited.
            cleanup = CleanupState.Unconfirmed;
        }
        finally
        {
            var report = new AcceptanceReport(runId, verdict?.Outcome, verdict?.ReasonCode, verdict?.Evidence,
                observation, ct.IsCancellationRequested, stop.IsCancellationRequested && !ct.IsCancellationRequested, cleanup,
                ProtocolRestored: false, OriginalConfigUnchanged: false, startError);
            WriteEvidence(reportPath, report);
            status?.Invoke("验收报告 " + reportPath);
            restored = cleanup is (CleanupState.Confirmed or CleanupState.NotRequired)
                && TryRestore(program, journal, reportPath, report);
            if (restored)
            {
                lease.ConfirmClean();
                File.Delete(recovery);
            }
        }
        var observed = observation is { OwnedPrimary: true, PlannedTasksVerified: true, FlowEnded: true,
            ForeignActivitySeen: false, AdverseSignal: false, ExitCode: 0 };
        return new(runId, verdict, cleanup, restored, observed, ct.IsCancellationRequested,
            stop.IsCancellationRequested && !ct.IsCancellationRequested, reportPath, startError)
        {
            DailyRewardClaimUnconfirmed = observation?.DailyRewardClaimUnconfirmed == true,
        };
    }

    private static async Task<GenshinVerifiedWorld> ConfirmSameWorldAsync(GenshinVerifiedWorld previous,
        Func<CancellationToken, Task<GenshinVerifiedWorld?>> recheck, CancellationToken ct)
    {
        var current = await recheck(ct).WaitAsync(ct).ConfigureAwait(false);
        if (current is null || !MatchesVerifiedWorld(previous, current.AccountId, current.WindowHandle, current.ProcessId, current.ProcessStartedAt) ||
            (previous.Uid is not null && previous.Uid != current.Uid))
            throw new InvalidOperationException("合成前后的原神游戏会话不一致。");
        return current;
    }

    private static RunVerdict WithCraftEvidence(RunVerdict verdict, Guid runId, int crafted)
    {
        var detail = crafted == 0 ? "库存已证实无需合成。" : $"库存变化证实已合成 {crafted} 个浓缩树脂。";
        var evidence = new Evidence(runId, BetterGiContract.CompletionSignalsVerified ? EvidenceKind.CompletionReported : EvidenceKind.Note,
            detail, DateTimeOffset.UtcNow);
        return BetterGiContract.CompletionSignalsVerified
            ? new(RunOutcome.Success, ReasonCodes.Completed, [.. verdict.Evidence, evidence])
            : verdict with { Evidence = [.. verdict.Evidence, evidence] };
    }

    internal static string ValidateDailyFiles(string root, Guid accountId, BetterGiPlanFiles expected, bool allowStockCraft = false)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var name = BetterGiAccountPlan.SourceProfileName(accountId);
        if (expected.ProfileName != name)
            throw new InvalidDataException("隔离任务档案与指定原神账号不匹配。");

        var user = Path.Combine(root, "User");
        var profilePath = Path.Combine(user, "OneDragon", name + ".json");
        var settingsPath = Path.Combine(user, "config.json");
        using var profile = ReadConfig(profilePath);
        using var settings = ReadConfig(settingsPath);
        var game = ReadGamePath(settings.RootElement, name);
        var fields = profile.RootElement;
        var enabled = ReadMember(fields, "TaskEnabledList");
        if (!allowStockCraft && enabled.ValueKind == JsonValueKind.Object &&
            enabled.TryGetProperty("genshin.craft_condensed_resin", out var craft) && craft.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("合成任务没有独立库存核验，已阻断启动。");
        var canonical = BetterGiAccountPlans.RebuildSavedRun(accountId, game, fields);
        if (!JsonEqual(profile, expected.ProfileJson) || !JsonEqual(settings, expected.SettingsJson)
            || !JsonEqual(profile, canonical.ProfileJson) || !JsonEqual(settings, canonical.SettingsJson))
            throw new InvalidDataException("隔离设置与本次账号已渲染的受控档案不一致。");
        return game;

        static bool JsonEqual(JsonDocument actual, string rendered)
        {
            using var candidate = JsonDocument.Parse(rendered, StrictJson);
            return JsonElement.DeepEquals(actual.RootElement, candidate.RootElement);
        }
    }

    private static string ReadGamePath(string settingsPath, string profileName)
    {
        using var json = ReadConfig(settingsPath);
        return ReadGamePath(json.RootElement, profileName);
    }

    private static string ReadGamePath(JsonElement settings, string profileName)
    {
        if (ReadMember(ReadMember(settings, "commonConfig"), "runForVersion").GetString() != BetterGiContract.PinnedTag)
            throw new InvalidDataException("隔离安装的运行版本标记不符，避免上游启动时放开目录权限。");
        if (ReadMember(settings, "selectedOneDragonFlowConfigName").GetString() != profileName)
            throw new InvalidDataException("隔离配置当前选中的任务档案不符。");
        var startup = ReadMember(settings, "genshinStartConfig");
        if (ReadMember(startup, "linkedStartEnabled").GetBoolean()
            || ReadMember(startup, "startGameWithCmd").GetBoolean()
            || ReadMember(startup, "autoEnterGameEnabled").GetBoolean()
            || ReadMember(startup, "autoDisableGenshinHdrEnabled").GetBoolean())
            throw new InvalidDataException("隔离配置必须关闭游戏联动启动、自动登录和 HDR 修改。");
        var game = ReadMember(startup, "installPath").GetString() ?? "";
        if (!Path.IsPathFullyQualified(game))
            throw new InvalidDataException("隔离配置缺少原神客户端完整路径。");
        return game;
    }

    private static JsonElement ReadMember(JsonElement document, string name) =>
        document.ValueKind == JsonValueKind.Object && document.TryGetProperty(name, out var member)
            ? member : throw new InvalidDataException($"隔离配置缺少字段 {name}。");

    private static JsonDocument ReadConfig(string path)
    {
        using var file = OpenVerifiedFile(path);
        if (file.Length > BetterGiConfiguration.MaxConfigBytes)
            throw new InvalidDataException("隔离设置超过大小限制。");
        return JsonDocument.Parse(file, StrictJson);
    }

    internal static async Task<int> RecoverAsync(string stage, CancellationToken ct = default)
    {
        string program;
        (stage, _, program) = await ValidateStageAsync(stage, ct).ConfigureAwait(false);
        var journal = ReadJournal(stage);
        using var lease = DesktopOperationLease.Acquire(journal.LockOwner ?? stage, recovery: true);
        await RecoverWithLeaseAsync(stage, program, journal, lease, ct).ConfigureAwait(false);
        return 0;
    }

    internal static async Task<Guid?> RecoverPendingForOwnerAsync(string ownerDirectory, CancellationToken ct,
        Func<Guid, CancellationToken, Task>? beforeRelease = null)
    {
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请在同一 Windows 用户的管理员会话中恢复原神验收。");
        if (!Path.IsPathFullyQualified(ownerDirectory))
            throw new ArgumentException("原神验收恢复需要桌面锁的绝对档案目录。", nameof(ownerDirectory));
        var owner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownerDirectory));
        ct.ThrowIfCancellationRequested();
        using var lease = DesktopOperationLease.TryAcquireRecovery(ownerDirectory);
        if (lease is not { Recovering: true }) return null;
        var found = FindPendingForOwner(owner, ct)
            ?? throw new InvalidOperationException("本档案的原神桌面锁仍待恢复，但找不到对应的验收记录。");
        var validated = await ValidateStageAsync(found.Stage, ct).ConfigureAwait(false);
        await RecoverWithLeaseAsync(validated.Stage, validated.Program, found.Journal, lease, ct, beforeRelease).ConfigureAwait(false);
        return found.Journal.RunId;
    }

    private static (string Stage, RecoveryJournal Journal)? FindPendingForOwner(string owner, CancellationToken ct)
    {
        using var matches = FindRecoveryJournals(owner, ct).GetEnumerator();
        if (!matches.MoveNext()) return null;
        var pending = matches.Current;
        if (matches.MoveNext())
            throw new InvalidOperationException("原神验收档案存在多份待恢复记录，无法确认应恢复哪次运行。");
        return pending;
    }

    internal static void RemoveInactiveRecoveryJournals(DesktopOperationLease lease, CancellationToken ct)
    {
        // A clean exclusive marker precedes any new run. Leftover journals can only predate its dirty mark or follow completed recovery.
        var owner = lease.VerifyCommonOwner(requireClean: true);
        foreach (var (stage, _) in FindRecoveryJournals(owner, ct))
            File.Delete(Path.Combine(stage, RecoveryFile));
    }

    private static IEnumerable<(string Stage, RecoveryJournal Journal)> FindRecoveryJournals(string owner, CancellationToken ct)
    {
        string[] stages;
        try { stages = Directory.GetDirectories(StageParent); }
        catch (DirectoryNotFoundException) { stages = []; }
        foreach (var stage in stages)
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            try { File.GetAttributes(Path.Combine(stage, RecoveryFile)); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            EnsureProtectedStage(StageParent, stage);
            var journal = ReadJournal(stage);
            if (!string.Equals(journal.LockOwner ?? stage, owner, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (stage, journal);
        }
    }

    private static RecoveryJournal ReadJournal(string stage)
    {
        using var file = OpenVerifiedFile(Path.Combine(stage, RecoveryFile));
        return ReadEvidence<RecoveryJournal>(file);
    }

    private static async Task RecoverWithLeaseAsync(string stage, string program, RecoveryJournal journal,
        DesktopOperationLease lease, CancellationToken ct, Func<Guid, CancellationToken, Task>? beforeRelease = null)
    {
        var owner = lease.VerifyCommonOwner();
        if (!JournalMatches(journal, program) || !string.Equals(journal.LockOwner ?? stage, owner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("待恢复记录与隔离安装、原协议、原配置摘要或桌面锁不符。");
        if (!lease.Recovering)
            throw new InvalidOperationException("该验收目录没有待恢复的桌面锁标记。");
        if (!string.Equals(stage, owner, StringComparison.OrdinalIgnoreCase)
            && (FindPendingForOwner(owner, ct) is not { } match
                || !string.Equals(match.Stage, stage, StringComparison.OrdinalIgnoreCase) || match.Journal != journal))
            throw new InvalidDataException("原神验收恢复目标在取得桌面锁前发生变化。");
        var recovery = Path.Combine(stage, RecoveryFile);
        using (var journalFile = OpenVerifiedFile(recovery))
        {
            if (ReadEvidence<RecoveryJournal>(journalFile) != journal)
                throw new InvalidDataException("待恢复记录在取得桌面锁前发生变化。");
            var reportPath = ReportPath(stage, journal.RunId);
            AcceptanceReport report;
            using (var reportFile = OpenVerifiedFile(reportPath))
                report = ReadEvidence<AcceptanceReport>(reportFile);
            if (!HasRecoveryProof(report, journal.RunId))
                throw new InvalidDataException("验收报告缺少本次运行已清理的证据，保留待恢复状态。");
            if (beforeRelease is not null && GenshinWindowsLoginDesktop.HasRunningClient())
                throw new InvalidOperationException("仍检测到原神客户端，请先退出后再核对桌面交接。");
            ct.ThrowIfCancellationRequested();
            if (!TryRestore(program, journal, reportPath, report))
                throw new InvalidOperationException("仍有 BetterGI 活动，或原配置、协议注册存在冲突，保留待恢复状态。");
        }
        if (beforeRelease is not null)
        {
            await beforeRelease(journal.RunId, ct).ConfigureAwait(false);
            if (GenshinWindowsLoginDesktop.HasRunningClient())
                throw new InvalidOperationException("原神客户端状态已变化，保留桌面交接标记。");
        }
        lease.ConfirmClean();
        File.Delete(recovery);
    }

    internal static bool HasRecoveryProof(AcceptanceReport report, Guid runId)
    {
        if (report.RunId != runId) return false;
        if (report.UserCancelled || report.TimedOut)
            return report.Cleanup == CleanupState.Confirmed && report.StartError is null
                && report.Observation is { ProcessId: > 0, OwnedPrimary: true, ForeignActivitySeen: false } stopped
                && stopped.ProcessStartedAt > DateTimeOffset.UnixEpoch
                && report.Evidence is { Count: > 0 } && report.Evidence.All(e => e is not null && e.RunId == runId);
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
        && journal.OriginalConfigRevision is { Length: 64 } revision && revision.All(Uri.IsHexDigit)
        && (journal.LockOwner is null || Path.IsPathFullyQualified(journal.LockOwner));

    private static bool TryRestore(string program, RecoveryJournal journal, string reportPath, AcceptanceReport report)
    {
        using var config = OpenVerifiedFile(OriginalConfig);
        if (FileRevision(config) != journal.OriginalConfigRevision || IsBetterGiBusy(program)) return false;

        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var scheme = registry.OpenSubKey(ProtocolRoot);
        using var protocol = registry.OpenSubKey(ProtocolCommand);
        if (!HasExpectedScheme(scheme)) return false;
        var current = ReadRegistryString(protocol, string.Empty);
        if (current == journal.Expected)
        {
            using var writable = registry.OpenSubKey(ProtocolCommand, writable: true);
            if (writable is null || IsBetterGiBusy(program) || !HasExpectedScheme(scheme)
                || ReadRegistryString(writable, string.Empty) != journal.Expected) return false;
            writable.SetValue(string.Empty, journal.Original, RegistryValueKind.String);
            writable.Flush();
        }
        else if (current != journal.Original) return false;

        if (IsBetterGiBusy(program) || !HasExpectedScheme(scheme)
            || ReadRegistryString(protocol, string.Empty) != journal.Original) return false;
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
        return true;
    }

    internal static void RequireLegacyLauncherClosed()
    {
        if (FakeEngineAdapter.ForeignProcessIds(@"E:\auto agent game\VIvian\VIvian.exe").Any(pid => pid != Environment.ProcessId))
            throw new InvalidOperationException("请先退出其他 furina 实例，旧版不参与共同桌面锁。");
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
        // The validated stage already provides the administrator-only ACL. Verify it on the newly created file.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (!IsAdministratorOnly(file.GetAccessControl()) || !ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, path) ||
            ReadOnlyFileAccess.LinkCount(file.SafeFileHandle) != 1)
            throw new InvalidDataException("无法核对新建验收证据的权限、路径或链接数。");
        JsonSerializer.Serialize(file, evidence, EvidenceJson);
        file.Flush(flushToDisk: true);
    }

    private sealed record RecoveryJournal(Guid RunId, string Original, string Expected, string OriginalConfigRevision,
        string? LockOwner = null);

    internal sealed record AcceptanceReport(Guid RunId, RunOutcome? Outcome, string? ReasonCode, IReadOnlyList<Evidence>? Evidence,
        BetterGiRunObservation? Observation, bool UserCancelled, bool TimedOut, CleanupState Cleanup,
        bool ProtocolRestored, bool OriginalConfigUnchanged, string? StartError);

    private static async Task<(string Stage, string Root, string Program)> ValidateStageAsync(string stage, CancellationToken ct)
    {
        if (!WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请在同一 Windows 用户的管理员终端中运行原神验收或恢复。");

        stage = Path.GetFullPath(stage);
        var parent = StageParent;
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
        if (Convert.ToHexString(await SHA256.HashDataAsync(executable, ct).ConfigureAwait(false)) != ProgramDigest)
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

    private static void VerifyActiveGame(string expectedGame, Guid? accountId, GenshinVerifiedWorld? world)
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
                || !string.Equals(ReadGameExecutable(session[0].Id), expectedGame, StringComparison.OrdinalIgnoreCase)
                || (accountId is { } id && !MatchesVerifiedWorld(world, id, foreground, session[0].Id,
                    StarRailWindow.StartTime(session[0])))
                || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("验收需要同一会话仅有一个指定的原神客户端，且它在前台。");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal static bool MatchesVerifiedWorld(GenshinVerifiedWorld? world, Guid accountId, IntPtr window,
        int processId, DateTimeOffset? processStartedAt) => world is not null && world.AccountId == accountId
        && world.WindowHandle == window && world.ProcessId == processId && world.ProcessStartedAt == processStartedAt;

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
