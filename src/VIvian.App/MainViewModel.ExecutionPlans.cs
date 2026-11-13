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
            () => { if (Queue.IsRunning) Queue.EmergencyStop(); });
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
        if (!PersistentPlanReady || !card.CanRunForReal) throw new InvalidOperationException("计划运行条件已变化。");
        var current = await _planReader.ReadAsync(new(item.Game, item.AccountId), item.Ordinal, card.InstallPath, ct);
        if (current != item) throw new InvalidOperationException("账号、任务或资源配置版本已变化，请重新确认。");
    }

    // Called while the existing home operation gate is held. Local daily execution does not depend on AI opt-in.
    private async Task<int?> RunPersistentHomePlanAsync(IReadOnlyList<GameAccount> selected)
    {
        if (!PersistentPlanReady) { BatchStatus = "请先保存配置并完成待处理的清理。"; return null; }
        await Accounts.SaveSelectionsAsync(CancellationToken.None);
        var items = new List<ExecutionPlanItem>();
        foreach (var account in selected)
        {
            var card = Engines.Single(x => x.Id == account.Game);
            if (!card.CanRunForReal) { BatchStatus = $"{card.Name}尚未通过运行检查。"; return null; }
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
            BatchStatus = $"计划 {plan.PlanId:D}：{receipt?.State.ToString() ?? "回执不可用"}；可按编号查回。";
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
        await ValidatePersistentItemAsync(receipt.Item, ct);
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
            BatchStatus = receipt is null ? $"计划 {id:D} 回执暂不可读取。" :
                $"计划 {id:D}：{receipt.State}。按计划编号可查回逐项结果。";
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
