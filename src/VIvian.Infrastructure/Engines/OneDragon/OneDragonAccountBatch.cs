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
        CancellationToken ct)
    {
        if (!RealExecutionPolicy.IsM5DailyTest || !WindowsProcessSupervisor.IsCurrentProcessElevated())
            throw new InvalidOperationException("请从原入口以管理员身份打开支持绝区零实测的 Vivian。");
        if (accounts.Count == 0 || accounts.Any(account => account.Game != Game || !account.Enabled || !account.AutoRun))
            throw new InvalidOperationException("请在绝区零账号管理勾选已经绑定的档案。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        using var lease = DesktopOperationLease.Acquire(profileDirectory);
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
        var orchestrator = new MultiAccountOrchestrator(database, driver);
        var batchId = Guid.NewGuid();
        var handoffClean = true;
        try
        {
            return await orchestrator.ExecuteAsync(Game, accounts, AccountConfigurationMode.Independent,
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
                        root, batchId, handoff, token).ConfigureAwait(false);
                    handoffClean = result.CleanupConfirmed;
                    return result;
                }, progress, ct, actionId: batchId).ConfigureAwait(false);
        }
        finally
        {
            if (handoffClean && !driver.LeavesGameForUser &&
                (await driver.StopGameAsync(CancellationToken.None).ConfigureAwait(false)).Ok)
                lease.ConfirmClean();
        }
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

    private const string BridgeDigest = "41C671182B5BF210717F4003A236B07C4CE7963A55FBDE09B1277FBA4EA5EEF4";

    private static async Task<BatchTaskResult> RunAsync(IRunJournal journal, OneDragonAdapter adapter, IProcessSupervisor supervisor,
        string root, Guid batchId, OneDragonHandoff handoff, CancellationToken ct)
    {
        var context = new RunContext(Guid.NewGuid(), batchId, Game, DateTimeOffset.UtcNow);
        Task Update(RunPhase phase, RunOutcome? outcome, CleanupState cleanup, string? reason) =>
            journal.PublishAsync(new(context.RunId, batchId, Game, RuleVersion, false, phase, outcome, cleanup,
                reason, DateTimeOffset.UtcNow), CancellationToken.None);
        await Update(RunPhase.Starting, null, CleanupState.Pending, null).ConfigureAwait(false);
        IEngineRun run;
        try
        {
            if (new OneDragonProcessProbe().CountRunning(Path.Combine(root, OneDragonContract.EntryProgram)) > 0)
                throw new EngineStartException(ReasonCodes.ForeignProcess, true, "请先退出 OneDragon。");
            run = await adapter.LaunchAsync(context, supervisor, ct, root, handoff).ConfigureAwait(false);
        }
        catch (EngineStartException ex)
        {
            var cleanup = ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed;
            await Update(RunPhase.Blocked, RunOutcome.Indeterminate, cleanup, ex.ReasonCode).ConfigureAwait(false);
            return new(context.RunId, false, ex.NothingStarted, true, ex.Message, RunOutcome.Indeterminate);
        }

        await using (run.ConfigureAwait(false))
        {
            var events = ReadEventsAsync();
            RunVerdict verdict;
            try
            {
                await Update(RunPhase.Running, null, CleanupState.Pending, null).ConfigureAwait(false);
                verdict = await run.WaitForVerdictAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await run.RequestStopAsync(StopMode.Emergency, stop.Token).ConfigureAwait(false);
                try { await run.WaitForVerdictAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                verdict = new(RunOutcome.Cancelled, ReasonCodes.UserStop, []);
            }
            var cleanup = await run.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            await events.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            var clean = cleanup == CleanupState.Confirmed;
            await Update(clean ? RunPhase.Finished : RunPhase.Blocked, verdict.Outcome, cleanup, verdict.ReasonCode).ConfigureAwait(false);
            var detail = verdict.Outcome switch
            {
                RunOutcome.Success => "OneDragon 已报告此账号的所有任务完成。",
                RunOutcome.Failed => "OneDragon 报告执行失败，详情见运行日志。",
                RunOutcome.Cancelled => "用户停止了 OneDragon。",
                _ => "OneDragon 未报告此账号的全部任务完成，请查看运行日志。",
            };
            return new(context.RunId, verdict.Outcome == RunOutcome.Success, clean, true, detail, verdict.Outcome);

            async Task ReadEventsAsync()
            {
                await foreach (var entry in run.Events.ConfigureAwait(false))
                    await journal.AppendEventsAsync([entry], CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
