using System.Security.Cryptography;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Delegates gameplay to the original installation; Vivian owns only scheduling and account switching.</summary>
internal static class BetterGiNativeDaily
{
    private const string ProgramDigest = "73732F440BC8FF3B0272F2ADBBF72ACADCB952D45BF7F5A62CF0E3573DBD7D73";
    private sealed record Pending(Guid RunId, Guid AccountId, string Root);

    internal static async Task<Guid?> RecoverAsync(string owner, CancellationToken ct,
        Func<Guid, CancellationToken, Task>? beforeRelease = null)
    {
        var pendingPath = Path.Combine(owner, "native-recovery.json");
        if (!File.Exists(pendingPath)) return null;
        using var lease = DesktopOperationLease.Acquire(owner, recovery: true);
        Pending pending;
        using (var file = ReadOnlyFileAccess.OpenForStableRead(pendingPath))
        {
            if (file.Length > 8192 || ReadOnlyFileAccess.FindReparsePoint(pendingPath) is not null)
                throw new InvalidDataException("原生引擎恢复记录无效。");
            pending = await JsonSerializer.DeserializeAsync<Pending>(file, cancellationToken: ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("原生引擎恢复记录为空。");
        }
        await ValidateInstallationAsync(pending.Root, ct).ConfigureAwait(false);
        if (GenshinWindowsLoginDesktop.HasRunningClient())
            throw new InvalidOperationException("请先关闭原神，再核对上一轮的引擎与配置恢复。");
        var profileRestore = Profile(pending.Root, pending.AccountId).Restore();
        var domainRestore = new BetterGiDomainOverride(pending.Root, new BetterGiProcessProbe()).Restore();
        if (profileRestore.State == RestoreState.Blocked || domainRestore.State == RestoreState.Blocked)
            throw new InvalidOperationException("BetterGI 结束后动作存在冲突，保留了当前设置和恢复记录。");
        if (beforeRelease is not null) await beforeRelease(pending.RunId, ct).ConfigureAwait(false);
        BetterGiRunRegistry.Shared.Release(pending.RunId, CleanupState.Confirmed);
        lease.ConfirmClean();
        File.Delete(pendingPath);
        return pending.RunId;
    }

    internal static async Task ValidateInstallationAsync(string root, CancellationToken ct)
    {
        var program = Path.Combine(root, BetterGiContract.ProgramName);
        using var file = ReadOnlyFileAccess.OpenForStableRead(program);
        if (ReadOnlyFileAccess.FindReparsePoint(program) is not null ||
            Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != ProgramDigest)
            throw new InvalidOperationException("BetterGI 版本与当前日志适配版本不一致，请先核对引擎版本。");
        if (new BetterGiProcessProbe().IsBusy(program))
            throw new InvalidOperationException("请先关闭独立运行的 BetterGI，再从 Vivian 启动账号批次。");
    }

    internal static BetterGiProfileOverride Profile(string root, Guid accountId) =>
        new(root, BetterGiAccountPlan.SourceProfileName(accountId), new BetterGiProcessProbe());

    internal static bool ReportedComplete(BetterGiRunObservation? observation) =>
        observation is { OwnedPrimary: true, FlowEnded: true,
            ForeignActivitySeen: false, AdverseSignal: false, ExitCode: 0 };

    internal static async Task<BetterGiDailyAcceptanceResult> RunAsync(string root, Guid accountId,
        string revision, string reportDirectory, DesktopOperationLease lease,
        Func<CancellationToken, Task> confirmGate, CancellationToken ct,
        IReadOnlyDictionary<string, string>? accountParameters = null)
    {
        lease.VerifyCommonOwner();
        if (lease.Recovering) throw new InvalidOperationException("请先恢复未完成的批次。");
        var profileName = BetterGiAccountPlan.SourceProfileName(accountId);
        var plan = await BetterGiConfiguration.InspectOneDragonAsync(root, profileName, ct).ConfigureAwait(false);
        if (plan.EnabledTasks.Count == 0) throw new InvalidOperationException("该账号的 BetterGI 档案没有启用任务。");
        var overlay = Profile(root, accountId);
        var domainOverlay = new BetterGiDomainOverride(root, new BetterGiProcessProbe());
        var runId = Guid.NewGuid();
        Directory.CreateDirectory(reportDirectory);
        var report = Path.Combine(reportDirectory, runId.ToString("N") + ".json");
        var pendingPath = Path.Combine(Path.GetDirectoryName(reportDirectory)!, "native-recovery.json");
        var pendingStage = pendingPath + "." + runId.ToString("N") + ".tmp";
        try
        {
            using (var pending = new FileStream(pendingStage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(pending, new Pending(runId, accountId, root));
                pending.Flush(flushToDisk: true);
            }
            File.Move(pendingStage, pendingPath, overwrite: false);
        }
        finally { File.Delete(pendingStage); }
        RunVerdict? verdict = null;
        BetterGiRunObservation? observation = null;
        var cleanup = CleanupState.NotRequired;
        var restored = false;
        string? startError = null;
        string? startErrorDetail = null;
        try
        {
            lease.MarkDirty();
            domainOverlay.Apply(plan.EnabledTasks.Contains(BetterGiMarkers.DomainTask) && accountParameters is not null
                ? accountParameters : new Dictionary<string, string>(), handoffAtGate: true);
            // Preserve every upstream setting, including teams, scripts, capture and teleport settings.
            overlay.Apply(revision, "关闭软件");
            var adapter = new BetterGiAdapter(() => root, BetterGiArguments.OneDragon(profileName).Request!) { DelegateNativeTasks = true };
            var context = new RunContext(runId, runId, GameId.Genshin, DateTimeOffset.UtcNow);
            cleanup = CleanupState.Unconfirmed;
            await using var run = await adapter.LaunchAsync(context,
                new WindowsProcessSupervisor(startMinimizedWithoutActivation: true), ct, confirmGate).ConfigureAwait(false);
            // The original engine already persists its logs; consume the event queue during arbitrarily long tasks.
            _ = DrainEventsAsync(run.Events);
            try
            {
                verdict = await run.WaitForVerdictAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await run.RequestStopAsync(StopMode.Emergency, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                cleanup = await run.ReconcileAsync(stop.Token).ConfigureAwait(false);
            }
            if (verdict is null && cleanup == CleanupState.Confirmed)
            {
                using var verdictLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { verdict = await run.WaitForVerdictAsync(verdictLimit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { cleanup = CleanupState.Unconfirmed; }
            }
            observation = ((BetterGiRun)run).Observe();
        }
        catch (EngineStartException ex)
        {
            startError = ex.ReasonCode;
            startErrorDetail = ex.Message;
            cleanup = ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed;
        }
        finally
        {
            if (cleanup is CleanupState.Confirmed or CleanupState.NotRequired)
            {
                var profileRestore = overlay.Restore();
                var domainRestore = domainOverlay.Restore();
                restored = profileRestore.State != RestoreState.Blocked && domainRestore.State != RestoreState.Blocked;
            }
            if (restored)
            {
                lease.ConfirmClean();
                File.Delete(pendingPath);
            }
        }
        var reported = ReportedComplete(observation);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            runId, root, profileName, observation, verdict, cleanup, restored,
            cancelled = ct.IsCancellationRequested, timedOut = false,
            completionBasis = "原始 BetterGI 进程日志；合成流程结束不代表独立库存核验", startError, startErrorDetail,
        }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(false);
        return new(runId, verdict, cleanup, restored, reported, ct.IsCancellationRequested,
            false, report, startError)
        {
            DailyRewardClaimUnconfirmed = observation?.DailyRewardClaimUnconfirmed == true,
            StartErrorDetail = startErrorDetail,
        };
    }

    private static async Task DrainEventsAsync(IAsyncEnumerable<RunEvent> events)
    {
        try { await foreach (var item in events.ConfigureAwait(false)) { } }
        catch (OperationCanceledException) { }
    }
}
