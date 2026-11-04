namespace VIvian.Core.Execution;

/// <summary>
/// A one-run launch capability. The production supervisor must complete StartAsync synchronously:
/// creation, Job assignment and registration happen while this gate is held. A supervisor that returns
/// before ownership is established is rejected and cannot be used for a real engine.
/// </summary>
internal sealed class RunProcessScope(IProcessSupervisor inner, Guid runId) : IProcessSupervisor
{
    private readonly Lock _gate = new();
    private readonly List<ISupervisedProcessGroup> _groups = [];
    private bool _revoked;
    private bool _unsafeLaunch;
    private bool _launchUsed;

    public bool HasUnsafeLaunch { get { lock (_gate) return _unsafeLaunch; } }

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
            // The Windows implementation creates the suspended process and assigns it to its Job
            // before returning a completed task. Holding this gate closes the revoke/create race.
            var launch = inner.StartAsync(spec, runId, ct);
            if (!launch.IsCompleted)
            {
                _unsafeLaunch = true;
                _ = QuarantineLateLaunchAsync(launch);
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false,
                    "进程监管器未在启动调用返回前建立所有权。");
            }
            var group = launch.GetAwaiter().GetResult();
            if (group.RunId != runId)
            {
                _unsafeLaunch = true;
                _ = QuarantineGroupAsync(group);
                throw new EngineStartException(ReasonCodes.StartFailed, nothingStarted: false, "进程组 RunId 不匹配。");
            }
            _groups.Add(group);
            return Task.FromResult(group);
        }
    }

    // The scope is revoked before waiting for any adapter or I/O operation. No later call may launch.
    public async Task<bool> RevokeAsync(TimeSpan timeout)
    {
        ISupervisedProcessGroup[] groups;
        lock (_gate)
        {
            _revoked = true;
            groups = [.. _groups];
        }
        var confirmed = !HasUnsafeLaunch;
        foreach (var group in groups)
        {
            try
            {
                var termination = group.TerminateAllAsync(timeout, CancellationToken.None);
                if (await termination.WaitAsync(timeout, TimeProvider.System, CancellationToken.None).ConfigureAwait(false) != CleanupState.Confirmed)
                    confirmed = false;
            }
            catch (Exception) { confirmed = false; }
        }
        return confirmed;
    }

    private async Task QuarantineLateLaunchAsync(Task<ISupervisedProcessGroup> launch)
    {
        try { await QuarantineGroupAsync(await launch.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception) { /* unsafe launch remains latched; the run can never be acknowledged in this session */ }
    }

    private static async Task QuarantineGroupAsync(ISupervisedProcessGroup group)
    {
        try { await group.TerminateAllAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
        finally { await group.DisposeAsync().ConfigureAwait(false); }
    }
}
