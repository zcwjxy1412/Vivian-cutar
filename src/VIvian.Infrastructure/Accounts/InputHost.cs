namespace VIvian.Infrastructure.Accounts;

/// <summary>All synthetic input goes through this narrow interface so tests can replace it and the UI never P/Invokes.</summary>
public interface IInputHost : IAsyncDisposable
{
    Task<InputResponse> ForegroundAsync(StarRailWindowInfo window, CancellationToken cancellationToken);
    Task<InputResponse> ClickAsync(StarRailWindowInfo window, int x, int y, CancellationToken cancellationToken);
    /// <summary>Ends exactly this game process; the host re-checks the image name and, when known, the start time.</summary>
    Task<InputResponse> KillAsync(StarRailWindowInfo window, CancellationToken cancellationToken);
    Task<InputResponse> StartGameAsync(string gamePath, CancellationToken cancellationToken);
}

public enum InputHostFailureKind
{
    /// <summary>The user answered "否" or closed the UAC prompt.</summary>
    Refused,
    /// <summary>The prompt or the host did not answer in time.</summary>
    Timeout,
    /// <summary>The host was connected and then disappeared (crash, kill, broken pipe).</summary>
    Lost,
    /// <summary>Launch failed for another reason, or a previous failure in this batch is being reported again.</summary>
    Failed,
}

public sealed class InputHostUnavailableException(string message, InputHostFailureKind kind = InputHostFailureKind.Failed)
    : Exception(message)
{
    public InputHostFailureKind Kind { get; } = kind;
}

public enum InputHostState { Idle, Connected, Refused, Lost, Failed, Closed }

/// <summary>One live connection to an elevated host process.</summary>
public interface IInputChannel : IAsyncDisposable
{
    bool IsAlive { get; }
    Task<InputResponse> SendAsync(InputRequest request, CancellationToken cancellationToken);
    /// <summary>Asks the host to exit and returns true only when the host process is proven to have exited.</summary>
    Task<bool> ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>Batch lifecycle of the elevated host: at most one UAC prompt per batch, and no prompt loops.</summary>
public interface IInputHostSession
{
    InputHostState State { get; }
    int LaunchCount { get; }
    void BeginBatch();
    /// <summary>Shuts the host down. Returns false when the host exit could not be confirmed.</summary>
    Task<bool> EndBatchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts the elevated host lazily: scanning and registering accounts needs no UAC prompt. Within one batch the host
/// is started at most once. If the user refuses the prompt, or the host times out or disappears, the batch receives
/// a typed <see cref="InputHostUnavailableException"/> on every later call instead of a new prompt; a new batch is
/// required to ask again. This never weakens the UAC boundary and never falls back to unelevated clicks.
/// </summary>
public sealed class ElevatedInputHost : IInputHost, IInputHostSession
{
    private readonly Func<CancellationToken, Task<IInputChannel>> _launch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IInputChannel? _channel;
    private string _lastError = "";
    private bool _disposed;

    public ElevatedInputHost() : this((string?)null)
    {
    }

    public ElevatedInputHost(string? hostExecutable)
        : this(async ct => await InputHostClient.StartAsync(hostExecutable, ct).ConfigureAwait(false))
    {
    }

    public ElevatedInputHost(Func<CancellationToken, Task<IInputChannel>> launch) => _launch = launch;

    public InputHostState State { get; private set; } = InputHostState.Idle;
    public int LaunchCount { get; private set; }

    public void BeginBatch()
    {
        // A fresh batch may ask again after an earlier refusal; a still-connected host is reused, never relaunched.
        if (State is InputHostState.Refused or InputHostState.Lost or InputHostState.Failed or InputHostState.Closed)
            State = _channel is { IsAlive: true } ? InputHostState.Connected : InputHostState.Idle;
        LaunchCount = 0;
        _lastError = "";
    }

    public async Task<bool> EndBatchAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = _channel;
            _channel = null;
            State = InputHostState.Closed;
            if (channel is null) return true;
            bool exited;
            try { exited = await channel.ShutdownAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { exited = false; }
            await channel.DisposeAsync().ConfigureAwait(false);
            return exited;
        }
        finally { _gate.Release(); }
    }

    public Task<InputResponse> ForegroundAsync(StarRailWindowInfo window, CancellationToken cancellationToken) =>
        SendAsync(Request("foreground", window), cancellationToken);

    public Task<InputResponse> ClickAsync(StarRailWindowInfo window, int x, int y, CancellationToken cancellationToken) =>
        SendAsync(Request("click", window, x, y), cancellationToken);

    public Task<InputResponse> KillAsync(StarRailWindowInfo window, CancellationToken cancellationToken) =>
        SendAsync(new InputRequest("kill", window.Handle.ToInt64(), ProcessId: window.ProcessId,
            ProcessStartUtcTicks: window.ProcessStartedAt?.UtcTicks ?? 0), cancellationToken);

    public Task<InputResponse> StartGameAsync(string gamePath, CancellationToken cancellationToken) =>
        SendAsync(new InputRequest("start", Path: gamePath), cancellationToken);

    private static InputRequest Request(string op, StarRailWindowInfo window, int x = 0, int y = 0) =>
        new(op, window.Handle.ToInt64(), x, y, window.ProcessId);

    private async Task<InputResponse> SendAsync(InputRequest request, CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await channel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InputHostUnavailableException or IOException or ObjectDisposedException)
        {
            await MarkLostAsync(channel, ex.Message).ConfigureAwait(false);
            throw new InputHostUnavailableException(
                "管理员输入宿主已断开（" + ex.Message + "）。本批次已停止，不会自动重新弹出授权。", InputHostFailureKind.Lost);
        }
    }

    private async Task MarkLostAsync(IInputChannel channel, string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_channel, channel)) return;
            _channel = null;
            State = InputHostState.Lost;
            _lastError = reason;
        }
        finally { _gate.Release(); }
        try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private async Task<IInputChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsAlive: true } alive) return alive;
            if (_channel is not null)
            {
                var dead = _channel;
                _channel = null;
                State = InputHostState.Lost;
                _lastError = "管理员输入宿主进程已退出";
                try { await dead.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            switch (State)
            {
                case InputHostState.Refused:
                    throw new InputHostUnavailableException(
                        "本批次的管理员授权已被拒绝或取消；需要开始新的批次才会再次请求授权。", InputHostFailureKind.Refused);
                case InputHostState.Lost:
                    throw new InputHostUnavailableException(
                        $"管理员输入宿主已断开（{_lastError}）；需要开始新的批次才会再次请求授权。", InputHostFailureKind.Lost);
                case InputHostState.Failed:
                    throw new InputHostUnavailableException(
                        $"管理员输入宿主不可用（{_lastError}）；需要开始新的批次才会再次请求授权。", InputHostFailureKind.Failed);
            }

            LaunchCount++;
            try
            {
                _channel = await _launch(cancellationToken).ConfigureAwait(false);
                State = InputHostState.Connected;
                return _channel;
            }
            catch (InputHostUnavailableException ex)
            {
                State = ex.Kind == InputHostFailureKind.Refused ? InputHostState.Refused : InputHostState.Failed;
                _lastError = ex.Message;
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                State = InputHostState.Failed;
                _lastError = "用户在授权过程中停止了批次";
                throw;
            }
            catch (Exception ex)
            {
                State = InputHostState.Failed;
                _lastError = ex.Message;
                throw new InputHostUnavailableException("无法启动管理员输入宿主：" + ex.Message, InputHostFailureKind.Failed);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await EndBatchAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _disposed = true;
        _gate.Dispose();
    }
}
