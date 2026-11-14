using System.Security.Cryptography;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.OneDragon;

public static class OneDragonAccountBatch
{
    internal const string RuleVersion = "zzzod-v2.6.0-desktop-handoff";
    private static readonly GameId Game = GameId.ZenlessZoneZero;

    public static async Task<MultiAccountOutcome> ExecuteAsync(LocalDatabase database, IReadOnlyList<GameAccount> accounts,
        string installRoot, string profileDirectory, IProcessSupervisor supervisor, Action<BatchAccountProgress>? progress,
        CancellationToken ct, Guid? childBatchId = null,
        Func<Guid, Guid, CancellationToken, Task>? beforeRun = null, bool stopOnFailure = false,
        Func<CancellationToken, Task>? validateConfirmed = null)
    {
        if (!RealExecutionPolicy.IsM5DailyTest || !WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请从原入口以管理员身份打开支持绝区零实测的 Vivian。");
        if (accounts.Count == 0 || accounts.Any(account => account.Game != Game || !account.Enabled || !account.AutoRun))
            throw new InvalidOperationException("请在绝区零账号管理勾选已经绑定的档案。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        using var lease = DesktopOperationLease.Acquire(profileDirectory);
        if (validateConfirmed is not null) await validateConfirmed(ct).ConfigureAwait(false);
        await ValidateInstallationAsync(root, ct).ConfigureAwait(false);
        var bindings = new Dictionary<Guid, (OneDragonProfileBinding Binding, OneDragonSourceProfile Profile)>();
        foreach (var account in accounts)
        {
            var settings = await database.LoadAccountSettingsAsync(account.Id, ct).ConfigureAwait(false);
            var binding = OneDragonProfileBinding.FromSettings(settings.Values);
            if (binding is null || !string.Equals(binding.Root, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("勾选的账号缺少对应当前 OneDragon 安装的实例绑定。");
            var profile = await OneDragonSourceProfile.ReadAsync(binding, ct).ConfigureAwait(false);
            if (!profile.Tasks.Any(task => task.Enabled))
                throw new InvalidOperationException($"账号“{account.Name}”的 OneDragon 档案没有启用任务。");
            bindings.Add(account.Id, (binding, profile));
        }
        if (bindings.Values.Select(row => row.Binding.Instance).Distinct().Count() != accounts.Count)
            throw new InvalidOperationException("两个勾选账号绑定了同一个 OneDragon 实例。");
        var gamePaths = bindings.Values.Select(row => row.Profile.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (gamePaths.Length != 1) throw new InvalidOperationException("同一批次的 OneDragon 实例须指向同一个绝区零国服客户端。");
        var profiles = await database.LoadLoginProfilesAsync(Game, ct).ConfigureAwait(false);
        if (accounts.Any(account => !profiles.Any(profile => profile.AccountId == account.Id && profile.NormalizedPhone.Length > 0)))
            throw new InvalidOperationException("有账号尚未绑定登录画面的脱敏标识。");

        using var driver = new ZenlessLoginDriver(gamePaths[0], root, lease.MarkDirty);
        var orchestrator = new MultiAccountOrchestrator(database, driver, host: null,
            policy: new SwitchPolicy(LoginScreenTimeout: TimeSpan.FromMinutes(5)));
        var batchId = childBatchId ?? Guid.NewGuid();
        var handoffClean = true;
        var finalGameClean = false;
        MultiAccountOutcome outcome;
        try
        {
            outcome = await orchestrator.ExecuteAsync(Game, accounts, AccountConfigurationMode.Independent,
                (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
                async (account, _, token) =>
                {
                    using var pinnedEnvironment = ReadOnlyFileAccess.OpenForStableRead(Path.Combine(root, "config", "env.yml"));
                    using var pinnedLauncher = ReadOnlyFileAccess.OpenForStableRead(Path.Combine(root, "src", "zzz_od",
                        "application", "zzz_application_launcher.py"));
                    await ValidateInstallationAsync(root, token).ConfigureAwait(false);
                    var (binding, before) = bindings[account.Id];
                    var current = await OneDragonSourceProfile.ReadAsync(binding, token).ConfigureAwait(false);
                    if (before.Revision != current.Revision || before.GamePath != current.GamePath)
                        return new(null, false, true, true, "OneDragon 档案在批次期间发生变化。", RunOutcome.Indeterminate);
                    var request = OneDragonArguments.OneDragon(instances: [binding.Instance]).Request!;
                    var handoff = new OneDragonHandoff(driver.GamePid, binding.Instance,
                        current.Tasks.Where(task => task.Enabled).Select(task => task.Id).ToArray(),
                        profiles.Single(profile => profile.AccountId == account.Id).NormalizedPhone);
                    handoffClean = false;
                    var result = await RunAsync(database, new OneDragonAdapter(() => root, request), supervisor,
                        root, batchId, handoff, progress is null ? null : detail =>
                            progress(new(account.Id, account.Name, BatchRunState.RunningTasks, detail, 1, DateTimeOffset.UtcNow)),
                        token, beforeRun is null ? null : (runId, runToken) => beforeRun(account.Id, runId, runToken)).ConfigureAwait(false);
                    handoffClean = result.CleanupConfirmed;
                    return result;
                }, progress, ct, actionId: batchId, stopOnFailure: stopOnFailure).ConfigureAwait(false);
        }
        finally
        {
            if (handoffClean && !driver.LeavesGameForUser &&
                (await driver.StopGameAsync(CancellationToken.None).ConfigureAwait(false)).Ok)
            {
                finalGameClean = true;
                lease.ConfirmClean();
            }
        }
        return outcome with { GameCleanupConfirmed = outcome.GameCleanupConfirmed && finalGameClean,
            Completed = outcome.Completed && finalGameClean };
    }

    public static async Task ValidateInstallationAsync(string root, CancellationToken ct = default)
    {
        var report = await OneDragonInstallationInspector.InspectAsync(root, ct).ConfigureAwait(false);
        if (report.Compatibility != CompatibilityClass.IdentityVerified || report.ProgramPath is null
            || report.Checks.Any(check => check.State == CheckState.Failed && check.Id != "od:instances"))
            throw new InvalidOperationException("请核对 OneDragon v2.6.0 安装、固定源码和关闭代码更新。");
        var entry = Path.Combine(root, "src", "zzz_od", "application", "zzz_application_launcher.py");
        if (ReadOnlyFileAccess.FindReparsePoint(entry) is not null)
            throw new InvalidOperationException("OneDragon 源项目入口包含链接。");
        await using var file = ReadOnlyFileAccess.OpenForStableRead(entry);
        if (!ReadOnlyFileAccess.HandlePathMatches(file.SafeFileHandle, entry) || file.Length > 64 * 1024
            || Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != BridgeDigest)
            throw new InvalidOperationException("请先准备固定版 OneDragon 的 Vivian 交接入口。");
        await OneDragonInstalledSource.VerifyAsync(root, ct).ConfigureAwait(false);
    }

    private const string BridgeDigest = "04C1D2D88B85D6E7FF3B14836CC86F788382FB6055D66E5EB40C9E38C9649EC6";

    internal static async Task<BatchTaskResult> RunAsync(IRunJournal journal, OneDragonAdapter adapter, IProcessSupervisor supervisor,
        string root, Guid batchId, OneDragonHandoff handoff, Action<string>? progress, CancellationToken ct,
        Func<Guid, CancellationToken, Task>? beforeRun = null, TimeSpan? journalTimeout = null)
    {
        var context = new RunContext(Guid.NewGuid(), batchId, Game, DateTimeOffset.UtcNow);
        if (beforeRun is not null) await beforeRun(context.RunId, ct).ConfigureAwait(false);
        var writeLimit = journalTimeout ?? TimeSpan.FromSeconds(8);
        var writes = new OneDragonJournalBoundary(writeLimit);
        Task Update(RunPhase phase, RunOutcome? outcome, CleanupState cleanup, string? reason,
            CancellationToken token = default) =>
            writes.WriteAsync(writeToken => journal.PublishAsync(new(context.RunId, batchId, Game, RuleVersion, false,
                phase, outcome, cleanup, reason, DateTimeOffset.UtcNow), writeToken), token);
        await Update(RunPhase.Starting, null, CleanupState.Pending, null, ct).ConfigureAwait(false);
        IEngineRun run;
        try
        {
            if (new OneDragonProcessProbe().CountRunning(Path.Combine(root, OneDragonContract.EntryProgram)) > 0)
                throw new EngineStartException(ReasonCodes.ForeignProcess, true, "请先退出 OneDragon。");
            run = await adapter.LaunchAsync(context, supervisor, ct, root, handoff).ConfigureAwait(false);
        }
        catch (EngineStartException ex)
        {
            var startCleanup = ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed;
            try { await Update(RunPhase.Blocked, RunOutcome.Indeterminate, startCleanup, ex.ReasonCode).ConfigureAwait(false); }
            catch (Exception) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw(); }
            return new(context.RunId, false, ex.NothingStarted, true, ex.Message, RunOutcome.Indeterminate);
        }

        var events = Task.Run(ReadEventsAsync);
        _ = ObserveTaskAsync(events);
        RunVerdict verdict = new(RunOutcome.Indeterminate, ReasonCodes.NoCompletionEvidence, []);
        var cleanup = CleanupState.Unconfirmed;
        Exception? failure = null;
        try
        {
            try
            {
                await Update(RunPhase.Running, null, CleanupState.Pending, null, ct).ConfigureAwait(false);
                var pendingVerdict = run.WaitForVerdictAsync(ct);
                _ = ObserveTaskAsync(pendingVerdict);
                // A failed event writer must stop an engine that is still doing work, not wait for its verdict.
                if (await Task.WhenAny(pendingVerdict, events).ConfigureAwait(false) == events)
                    await events.ConfigureAwait(false);
                verdict = await pendingVerdict.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                verdict = new(RunOutcome.Cancelled, ReasonCodes.UserStop, []);
            }
        }
        catch (Exception ex) { failure = ex; }

        if (failure is not null || ct.IsCancellationRequested)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await run.RequestStopAsync(StopMode.Emergency, stop.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        using (var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
        {
            try { cleanup = await run.ReconcileAsync(reconcile.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is null)
            try { await events.WaitAsync(writeLimit).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }

        // Release once on every path, retaining the original error if release also fails.
        try { await run.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure ??= ex; }
        if (failure is not null)
        {
            try
            {
                await Update(RunPhase.Blocked, verdict.Outcome, CleanupState.Unconfirmed,
                    ReasonCodes.CleanupUnconfirmed).ConfigureAwait(false);
            }
            catch (Exception) { /* Persistence may be the original fault; the desktop marker remains dirty. */ }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        var clean = cleanup == CleanupState.Confirmed;
        await Update(clean ? RunPhase.Finished : RunPhase.Blocked, verdict.Outcome, cleanup, verdict.ReasonCode).ConfigureAwait(false);
        var detail = verdict.Outcome switch
        {
            RunOutcome.Success => "OneDragon 已确认此账号的任务均已完成，含按源项目记录跳过的任务。",
            RunOutcome.Failed => "OneDragon 报告执行失败，详情见运行日志。",
            RunOutcome.Cancelled => "用户停止了 OneDragon。",
            _ => "OneDragon 未报告此账号的全部任务完成，请查看运行日志。",
        };
        return new(context.RunId, verdict.Outcome == RunOutcome.Success, clean, true, detail, verdict.Outcome);

        static async Task ObserveTaskAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { /* Observe a late journal failure without replacing the first error. */ }
        }

        async Task ReadEventsAsync()
        {
            await foreach (var entry in run.Events.ConfigureAwait(false))
            {
                await writes.WriteAsync(token => journal.AppendEventsAsync([entry], token)).ConfigureAwait(false);
                if (entry.Kind == RunEventKind.Progress) progress?.Invoke(entry.Message);
            }
        }
    }
}
