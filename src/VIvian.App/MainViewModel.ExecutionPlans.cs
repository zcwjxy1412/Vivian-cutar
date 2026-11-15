using System.Windows.Threading;
using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Engines.OneDragon;
using VIvian.Infrastructure.Processes;

namespace VIvian.App;

public sealed partial class MainViewModel
{
    private readonly Dispatcher _planDispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<Guid, PreparedExecutionPlan> _preparedPlans = [];
    private PersistentExecutionPlanCoordinator? _persistentPlans;
    private SavedExecutionPlanReader _planReader = null!;
    private Task _persistentPlanTask = Task.CompletedTask;
    private bool _persistentPlanFault;

    private void BlockPersistentPlans(string message)
    {
        _persistentPlanFault = true;
        Raise(nameof(RunStateText));
        BatchStatus = message + " 当前会话已停用新计划；核对清理记录后重新打开程序。";
        ShowStatus(BatchStatus, HomeStatusAction.ReviewRuns);
    }

    private void InitializePersistentPlans()
    {
        _planReader = new(Accounts.Database, _control);
        _persistentPlans = new(Accounts.Database,
            (item, beforeRun, ct) => OnPlanDispatcherAsync(() => RunPersistentItemAsync(item, beforeRun, ct)),
            () => { if (Queue.IsRunning) Queue.EmergencyStop(); },
            validateBeforeExecution: (item, ct) => OnPlanDispatcherAsync(async () =>
            { await ValidatePersistentItemAsync(item.Item, ct); return true; }));
    }

    Task<ExecutionPlanPreparation> IExecutionPlanService.PrepareAsync(IReadOnlyList<ExecutionPlanRequestItem> items, CancellationToken ct) =>
        OnPlanDispatcherAsync(() => PreparePersistentPlanAsync(items, ct));
    Task<ExecutionPlanSubmission> IExecutionPlanService.SubmitConfirmedAsync(PreparedExecutionPlan plan, CancellationToken ct) =>
        OnPlanDispatcherAsync(() => SubmitPersistentPlanAsync(plan, ct));
    Task<ExecutionPlanReceipt?> IExecutionPlanService.ReadAsync(Guid planId, CancellationToken ct) => Accounts.Database.ReadAsync(planId, ct);

    private Task<T> OnPlanDispatcherAsync<T>(Func<Task<T>> action) =>
        _planDispatcher.CheckAccess() ? action() : _planDispatcher.InvokeAsync(action).Task.Unwrap();

    private bool PersistentPlanReady => !_persistentPlanFault && RealExecutionPolicy.IsM5DailyTest && CanOperateWorkspace &&
        !Queue.HasUnresolved && !_control.HasPendingConfigurationRestore && Configuration.PendingCount == 0;

    private async Task<ExecutionPlanPreparation> PreparePersistentPlanAsync(IReadOnlyList<ExecutionPlanRequestItem> requests, CancellationToken ct)
    {
        if (!PersistentPlanReady || CanStopAll || !IsIdle || !Assistant.EnableScheduleProposals)
            return new(null, "host_not_ready");
        if (requests is null || requests.Count is < 1 or > 32 || requests.Distinct().Count() != requests.Count)
            return new(null, "invalid_plan_items");
        var items = new List<ExecutionPlanItem>();
        try
        {
            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                var card = Engines.SingleOrDefault(x => x.Id == request.Game);
                if (card?.CanRunForReal != true) return new(null, "game_not_ready");
                items.Add(await _planReader.ReadAsync(request, items.Count, card.InstallPath, ct));
            }
            var plan = new PreparedExecutionPlan(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), items.ToArray());
            foreach (var id in _preparedPlans.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                _preparedPlans.Remove(id);
            if (_preparedPlans.Count >= 100) return new(null, "too_many_pending_plans");
            _preparedPlans.Add(plan.PlanId, plan with { Items = plan.Items.ToArray() });
            return new(plan, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            Status = "无法准备运行计划：" + ex.Message;
            return new(null, "saved_configuration_not_ready");
        }
    }

    private async Task<ExecutionPlanSubmission> SubmitPersistentPlanAsync(PreparedExecutionPlan plan, CancellationToken ct)
    {
        if (await Accounts.Database.ReadAsync(plan.PlanId, ct) is not null)
            return await Accounts.Database.AcceptAsync(plan, ct); // Exact duplicate/conflict, even after expiry or restart.
        if (!_preparedPlans.TryGetValue(plan.PlanId, out var issued) || issued.ExpiresAt != plan.ExpiresAt ||
            !issued.Items.SequenceEqual(plan.Items) || plan.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(ExecutionPlanDisposition.Rejected, null, "plan_expired_or_not_prepared");
        if (!PersistentPlanReady || CanStopAll || !Assistant.EnableScheduleProposals ||
            !WindowsProcessSupervisor.IsCurrentProcessElevated())
            return new(ExecutionPlanDisposition.Rejected, null, "host_not_ready_or_elevation_required");
        if (!await _operation.WaitAsync(0, ct)) return new(ExecutionPlanDisposition.Rejected, null, "host_busy");
        Busy(true);
        var accepted = false;
        try
        {
            if (ShowPendingBatchRecovery()) return new(ExecutionPlanDisposition.Rejected, null, "desktop_recovery_required");
            foreach (var item in plan.Items) await ValidatePersistentItemAsync(item, ct);
            BatchRunning = true;
            var result = await _persistentPlans!.SubmitAsync(plan, ct);
            if (result.Disposition == ExecutionPlanDisposition.Accepted)
            {
                accepted = true;
                BatchStatus = "计划已持久保存：" + plan.PlanId.ToString("D");
                _persistentPlanTask = ObservePersistentPlanAsync(plan.PlanId);
            }
            return result;
        }
        finally
        {
            if (!accepted) { BatchRunning = false; Busy(false); _operation.Release(); }
        }
    }

    private async Task ValidatePersistentItemAsync(ExecutionPlanItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var card = Engines.Single(x => x.Id == item.Game);
        var issue = _persistentPlanFault ? ExecutionPlanPreflightIssue.SessionBlocked :
            Queue.HasUnresolved ? ExecutionPlanPreflightIssue.CleanupPending :
            _control.HasPendingConfigurationRestore ? ExecutionPlanPreflightIssue.ConfigurationRestorePending :
            Configuration.PendingCount > 0 ? ExecutionPlanPreflightIssue.UnsavedConfiguration :
            !PersistentPlanReady ? ExecutionPlanPreflightIssue.SessionBlocked :
            !card.CanRunForReal ? ExecutionPlanPreflightIssue.EngineNotReady : ExecutionPlanPreflightIssue.Unspecified;
        if (issue != ExecutionPlanPreflightIssue.Unspecified) throw new ExecutionPlanPreflightException("计划运行条件已变化。", issue);
        var current = await _planReader.ReadAsync(new(item.Game, item.AccountId), item.Ordinal, card.InstallPath, ct);
        if (current != item) throw new ExecutionPlanPreflightException("计划配置已变化。", ChangedPlanIssue(item, current));
    }

    internal static ExecutionPlanPreflightIssue ChangedPlanIssue(ExecutionPlanItem before, ExecutionPlanItem current) =>
        before.AccountId != current.AccountId || before.AccountName != current.AccountName ? ExecutionPlanPreflightIssue.AccountChanged :
        before.TaskPlanRevision != current.TaskPlanRevision || before.TaskId != current.TaskId ? ExecutionPlanPreflightIssue.TasksChanged :
        before.ResourcePolicy != current.ResourcePolicy || before.ResourceSummary != current.ResourceSummary ? ExecutionPlanPreflightIssue.ResourcesChanged :
        ExecutionPlanPreflightIssue.ConfigurationVersionChanged;

    internal static ExecutionPlanItemReceipt? SafePreflightFailure(ExecutionPlanReceipt? receipt) =>
        receipt?.ReasonCode == ReasonCodes.PreflightError && !receipt.Items.Any(x => x.Result is { } r &&
            (r.Cleanup == CleanupState.Unconfirmed || !r.ConfigurationRestored))
        ? receipt.Items.FirstOrDefault(x => x.RunIds.Count == 0 && x.Result is
            { Outcome: RunOutcome.Failed, Cleanup: CleanupState.NotRequired, ConfigurationRestored: true, ReasonCode: ReasonCodes.PreflightError }) : null;

    internal ExecutionPlanItem? StatusPlanItem { get; private set; }

    internal static string PersistentPlanCompletionText(Guid id, ExecutionPlanReceipt? receipt)
    {
        if (SafePreflightFailure(receipt) is { } failed)
        {
            var reason = failed.Result!.PreflightIssue switch
            {
                ExecutionPlanPreflightIssue.SessionBlocked => "本次会话运行条件已失效；查看运行记录后重新打开 furina",
                ExecutionPlanPreflightIssue.CleanupPending => "此前运行仍有清理记录待确认",
                ExecutionPlanPreflightIssue.ConfigurationRestorePending => "上次临时配置尚未恢复",
                ExecutionPlanPreflightIssue.UnsavedConfiguration => "星铁任务设置还有未保存的修改",
                ExecutionPlanPreflightIssue.EngineNotReady => "该游戏的引擎安装核验未通过，请打开引擎与环境",
                ExecutionPlanPreflightIssue.AccountChanged => "账号信息与确认时不一致，请打开账号管理核对",
                ExecutionPlanPreflightIssue.TasksChanged => "任务开关或任务方案与确认时不一致，请打开该账号的游戏设置",
                ExecutionPlanPreflightIssue.ResourcesChanged => "资源使用规则与确认时不一致，请打开该账号的游戏设置核对",
                ExecutionPlanPreflightIssue.ConfigurationVersionChanged => "账号或上游配置的版本与确认时不一致；需要检查配置变化，不能直接认定是你修改了设置",
                _ => "旧记录没有保存具体拦截原因，不能判断哪项配置变化；请生成诊断报告",
            };
            var game = failed.Item.Game switch { GameId.StarRail => "星铁", GameId.Genshin => "原神", _ => "绝区零" };
            return $"{game} · {failed.Item.AccountName} 尚未启动：{reason}。已完成 {receipt!.Items.Count(x => x.State == ExecutionPlanItemState.Succeeded)} 项；后续 {receipt.Items.Count(x => x.State == ExecutionPlanItemState.Skipped)} 项未执行。处理后重新确认未完成任务，避免重复运行已完成账号。";
        }
        return $"计划 {id:D}：{receipt?.State.ToString() ?? "回执不可用"}；可按编号查回。";
    }

    internal void SetPersistentPlanCompletion(Guid id, ExecutionPlanReceipt? receipt)
    {
        var failed = SafePreflightFailure(receipt);
        StatusPlanItem = failed?.Item;
        var action = failed?.Result?.PreflightIssue switch
        {
            ExecutionPlanPreflightIssue.CleanupPending => PendingRunAction,
            ExecutionPlanPreflightIssue.ConfigurationRestorePending => HomeStatusAction.RestoreConfiguration,
            ExecutionPlanPreflightIssue.UnsavedConfiguration => HomeStatusAction.ApplyConfiguration,
            ExecutionPlanPreflightIssue.EngineNotReady => HomeStatusAction.ConfigureEngines,
            ExecutionPlanPreflightIssue.AccountChanged => failed.Item.Game switch { GameId.StarRail => HomeStatusAction.SelectStarRailAccounts, GameId.Genshin => HomeStatusAction.SelectGenshinAccounts, _ => HomeStatusAction.SelectZenlessAccounts },
            ExecutionPlanPreflightIssue.TasksChanged or ExecutionPlanPreflightIssue.ResourcesChanged or ExecutionPlanPreflightIssue.ConfigurationVersionChanged => HomeStatusAction.ReviewPlanConfiguration,
            ExecutionPlanPreflightIssue.Unspecified => HomeStatusAction.ExportDiagnostics,
            ExecutionPlanPreflightIssue.SessionBlocked => HomeStatusAction.ReviewRuns,
            _ => HomeStatusAction.None,
        };
        SetBatchStatus(PersistentPlanCompletionText(id, receipt), action);
        ShowStatus(BatchStatus, action);
    }

    // Called while the existing home operation gate is held. Local daily execution does not depend on AI opt-in.
    private async Task<int?> RunPersistentHomePlanAsync(IReadOnlyList<GameAccount> selected)
    {
        if (!PersistentPlanReady)
        {
            if (ShowPendingBatchRecovery()) return null;
            if (Configuration.PendingCount > 0)
            { SetBatchStatus("星铁还有未保存的任务配置，请先应用修改。", HomeStatusAction.ApplyConfiguration); return null; }
            SetBatchStatus("当前运行条件未就绪，请检查引擎与配置；如本次会话发生过执行异常，请核对运行记录后重新打开程序。", HomeStatusAction.ReviewRuns);
            return null;
        }
        await Accounts.SaveSelectionsAsync(CancellationToken.None);
        var items = new List<ExecutionPlanItem>();
        foreach (var account in selected)
        {
            var card = Engines.Single(x => x.Id == account.Game);
            if (!card.CanRunForReal) { SetBatchStatus($"{card.Name}尚未通过运行检查：{card.Evidence}", HomeStatusAction.RepairEnginePaths); return null; }
            items.Add(await _planReader.ReadAsync(new(account.Game, account.Id), items.Count, card.InstallPath, CancellationToken.None));
        }
        var plan = new PreparedExecutionPlan(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), items.ToArray());
        BatchRunning = true;
        try
        {
            var submitted = await _persistentPlans!.SubmitAsync(plan);
            if (submitted.Disposition != ExecutionPlanDisposition.Accepted)
            { BatchStatus = "计划未接受：" + submitted.ReasonCode; return null; }
            BatchStatus = $"计划 {plan.PlanId:D} 已保存，按已勾选账号顺序执行。";
            await _persistentPlans.WhenIdleAsync();
            var receipt = await Accounts.Database.ReadAsync(plan.PlanId);
            SetPersistentPlanCompletion(plan.PlanId, receipt);
            if (receipt is null || receipt.Items.Any(x => x.Result is { } result &&
                    (result.Cleanup == CleanupState.Unconfirmed || !result.ConfigurationRestored)))
                BlockPersistentPlans($"计划 {plan.PlanId:D} 的清理或配置恢复未确认。");
            return receipt?.State == ExecutionPlanState.Succeeded ? 0 : 1;
        }
        catch
        {
            BlockPersistentPlans($"计划 {plan.PlanId:D} 持久记录未完成，已停止后续项目。");
            throw;
        }
        finally { BatchRunning = false; await Queue.RefreshAsync(); }
    }

    private async Task<MultiAccountOutcome> RunPersistentItemAsync(ExecutionPlanItemReceipt receipt,
        Func<Guid, CancellationToken, Task> beforeRun, CancellationToken ct)
    {
        // Initial validation is the coordinator's read-only preflight. Checks under each adapter's lease
        // remain execution-stage checks: a rejection there must never imply cleanup is unnecessary.
        var item = receipt.Item;
        var account = (await Accounts.Database.LoadAccountsAsync(item.Game, ct)).Single(x => x.Id == item.AccountId);
        var install = Engines.Single(x => x.Id == item.Game).InstallPath;
        void Progress(BatchAccountProgress progress) => _planDispatcher.Invoke(() =>
            BatchStatus = $"{item.Ordinal + 1} · {RunPresentation.GameName(item.Game)} · {progress.Detail}");
        Task Validate(CancellationToken token) => OnPlanDispatcherAsync(async () =>
        { await ValidatePersistentItemAsync(item, token); return true; });
        return item.Game switch
        {
            GameId.StarRail => await RunStarRailBatchAsync([account], null, Progress, ct, receipt, beforeRun),
            GameId.Genshin => await Task.Run(() => BetterGiAccountBatch.ExecuteSavedDailyAsync(Accounts.Database,
                [account], install, "", _paths.DataDirectory, Progress, ct, receipt.ChildBatchId,
                (_, run, token) => beforeRun(run, token), stopOnFailure: true, validateConfirmed: Validate), ct),
            GameId.ZenlessZoneZero => await Task.Run(() => OneDragonAccountBatch.ExecuteAsync(Accounts.Database,
                [account], install, _paths.DataDirectory, _zenlessSupervisor, Progress, ct, receipt.ChildBatchId,
                (_, run, token) => beforeRun(run, token), stopOnFailure: true, validateConfirmed: Validate), ct),
            _ => throw new InvalidOperationException("不支持的游戏。")
        };
    }

    private async Task ObservePersistentPlanAsync(Guid id)
    {
        try
        {
            await _persistentPlans!.WhenIdleAsync();
            var receipt = await Accounts.Database.ReadAsync(id);
            SetPersistentPlanCompletion(id, receipt);
            if (receipt is null || receipt.Items.Any(x => x.Result is { } result &&
                    (result.Cleanup == CleanupState.Unconfirmed || !result.ConfigurationRestored)))
                BlockPersistentPlans($"计划 {id:D} 的清理或配置恢复未确认。");
        }
        catch (Exception)
        { BlockPersistentPlans($"计划 {id:D} 的持久记录未完成，已禁止继续。"); }
        finally
        {
            BatchRunning = false;
            Busy(false);
            _operation.Release();
            try { await Queue.RefreshAsync(); } catch (Exception) { /* Receipt remains authoritative. */ }
        }
    }
}
