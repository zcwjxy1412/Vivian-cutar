namespace VIvian.Core.Execution;

/// <summary>
/// A one-run launch capability handed to the adapter instead of the real supervisor.
/// <list type="bullet">
/// <item>Revocation never waits for a launch: <see cref="RevokeLaunches"/> only takes a short internal lock that is
/// never held while the real supervisor runs, so an emergency-stop callback cannot be blocked by a slow launch.</item>
/// <item>A launch that completes after revocation is not handed to the adapter: it is kept for termination and the
/// caller is told something may have started.</item>
/// <item>Any launch whose ownership cannot be established (no completed result, a foreign RunId, or a failure other
/// than "nothing started") latches <see cref="HasUnsafeLaunch"/>; such a run is never reported as cleaned up.</item>
/// </list>
/// The production supervisor completes StartAsync synchronously (create suspended, assign to Job, resume), so the
/// "launch in progress" window is the duration of that call.
/// </summary>
internal sealed class RunProcessScope(IProcessSupervisor inner, Guid runId, TimeProvider? time = null) : IProcessSupervisor
{
    private readonly Lock _gate = new();
    private readonly List<ISupervisedProcessGroup> _groups = [];
    private readonly TaskCompletionSource _launchSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Task _quarantine = Task.CompletedTask;
    private bool _revoked;
    private bool _unsafeLaunch;
    private bool _launchUsed;
    private bool _launchInProgress;

    public bool HasUnsafeLaunch { get { lock (_gate) return _unsafeLaunch; } }

    public bool IsRevoked { get { lock (_gate) return _revoked; } }

    public void RevokeLaunches()
    {
        lock (_gate) _revoked = true;
    }

    public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid requestedRunId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_revoked || _launchUsed || requestedRunId != runId || ct.IsCancellationRequested)
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: true,
                    "运行许可已撤销、已使用，或进程请求的 RunId 不匹配。");
            _launchUsed = true;
            _launchInProgress = true;
        }

        try
        {
            Task<ISupervisedProcessGroup> launch;
            try
            {
                launch = inner.StartAsync(spec, runId, ct);
            }
            catch (EngineStartException ex) when (ex.NothingStarted)
            {
                throw; // the supervisor proved nothing was created
            }
            catch (Exception ex)
            {
                // e.g. Job assignment failed and the suspended process could not be confirmed gone.
                MarkUnsafe();
                throw ex as EngineStartException ?? new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false,
                    "进程监管器启动失败，无法确认没有进程被创建。");
            }

            if (!launch.IsCompleted)
            {
                MarkUnsafe();
                Quarantine(QuarantineLateLaunchAsync(launch));
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false,
                    "进程监管器未在启动调用返回前建立所有权。");
            }
            if (!launch.IsCompletedSuccessfully)
            {
                if (launch.Exception?.InnerException is EngineStartException { NothingStarted: true } nothing) throw nothing;
                MarkUnsafe();
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false,
                    "进程监管器启动失败，无法确认没有进程被创建。");
            }

            var group = launch.Result;
            lock (_gate)
            {
                if (group.RunId != runId) _unsafeLaunch = true;
                else
                {
                    // Registered even when revoked meanwhile, so RevokeAsync terminates it.
                    _groups.Add(group);
                    if (!_revoked) return Task.FromResult(group);
                }
            }
            if (group.RunId != runId)
            {
                Quarantine(QuarantineGroupAsync(group));
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "进程组 RunId 不匹配。");
            }
            // Revoked while the supervisor was creating the process: the adapter never receives it, and it is
            // terminated now rather than waiting for a later RevokeAsync (the run may already have been abandoned).
            Quarantine(QuarantineGroupAsync(group));
            throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false,
                "启动期间运行许可被撤销；已创建的进程将被终止。");
        }
        finally
        {
            lock (_gate) _launchInProgress = false;
            _launchSettled.TrySetResult();
        }
    }

    /// <summary>
    /// Revokes the capability, then terminates and releases every group this run owns. Returns true only when every
    /// group's termination was confirmed, no launch is still in progress and no launch had unknown ownership. Every
    /// wait is bounded by <paramref name="timeout"/>; a timeout is reported as false, never as cleaned up.
    /// </summary>
    public async Task<bool> RevokeAsync(TimeSpan timeout)
    {
        bool inProgress;
        lock (_gate)
        {
            _revoked = true;
            inProgress = _launchInProgress;
        }
        var confirmed = true;
        if (inProgress)
        {
            try { await _launchSettled.Task.WaitAsync(timeout, _time).ConfigureAwait(false); }
            catch (TimeoutException) { confirmed = false; }
        }

        ISupervisedProcessGroup[] groups;
        Task quarantine;
        lock (_gate)
        {
            groups = [.. _groups];
            quarantine = _quarantine;
            if (_unsafeLaunch || _launchInProgress) confirmed = false;
        }
        foreach (var group in groups)
        {
            Task<CleanupState>? termination = null;
            try
            {
                termination = group.TerminateAllAsync(timeout, CancellationToken.None);
                if (await termination.WaitAsync(timeout, _time).ConfigureAwait(false) != CleanupState.Confirmed)
                    confirmed = false;
            }
            catch (Exception)
            {
                termination?.ObserveLater();
                confirmed = false;
            }
            Task? release = null;
            try
            {
                // Releasing the group closes its Job handle (kill-on-close backup). Never proof of cleanup on its own.
                release = group.DisposeAsync().AsTask();
                await release.WaitAsync(timeout, _time).ConfigureAwait(false);
            }
            catch (Exception) { release?.ObserveLater(); /* termination result above decides */ }
        }
        try { await quarantine.WaitAsync(timeout, _time).ConfigureAwait(false); }
        catch (Exception) { quarantine.ObserveLater(); confirmed = false; }
        return confirmed;
    }

    private void MarkUnsafe()
    {
        lock (_gate) _unsafeLaunch = true;
    }

    private void Quarantine(Task task)
    {
        lock (_gate)
        {
            var previous = _quarantine;
            _quarantine = Task.WhenAll(previous, task);
        }
    }

    private static async Task QuarantineLateLaunchAsync(Task<ISupervisedProcessGroup> launch)
    {
        ISupervisedProcessGroup group;
        try { group = await launch.ConfigureAwait(false); }
        catch (Exception) { return; /* unsafe launch remains latched; the run can never be acknowledged in this session */ }
        await QuarantineGroupAsync(group).ConfigureAwait(false);
    }

    private static async Task QuarantineGroupAsync(ISupervisedProcessGroup group)
    {
        try { await group.TerminateAllAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* unsafe launch remains latched */ }
        finally
        {
            try { await group.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* kill-on-close is best effort; unsafe stays latched */ }
        }
    }
}
