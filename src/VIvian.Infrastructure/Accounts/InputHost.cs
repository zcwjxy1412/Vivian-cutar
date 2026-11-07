namespace VIvian.Infrastructure.Accounts;

/// <summary>All synthetic input goes through this narrow interface so tests can replace it and the UI never P/Invokes.</summary>
public interface IInputHost : IAsyncDisposable
{
    Task<InputResponse> ForegroundAsync(StarRailWindowInfo window, CancellationToken cancellationToken);
    Task<InputResponse> ClickAsync(StarRailWindowInfo window, int x, int y, CancellationToken cancellationToken);
    Task<InputResponse> KillAsync(int processId, CancellationToken cancellationToken);
    Task<InputResponse> StartGameAsync(string gamePath, CancellationToken cancellationToken);
}

/// <summary>Starts the elevated host lazily: scanning and registering accounts needs no UAC prompt.</summary>
public sealed class ElevatedInputHost : IInputHost
{
    private readonly string? _hostExecutable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InputHostClient? _client;
    private bool _disposed;

    public ElevatedInputHost() : this(null)
    {
    }

    public ElevatedInputHost(string? hostExecutable) => _hostExecutable = hostExecutable;

    public async Task<InputResponse> ForegroundAsync(StarRailWindowInfo window, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync(Request("foreground", window), cancellationToken).ConfigureAwait(false);
    }

    public async Task<InputResponse> ClickAsync(StarRailWindowInfo window, int x, int y, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync(Request("click", window, x, y), cancellationToken).ConfigureAwait(false);
    }

    public async Task<InputResponse> KillAsync(int processId, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync(new InputRequest("kill", ProcessId: processId), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<InputResponse> StartGameAsync(string gamePath, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.SendAsync(new InputRequest("start", Path: gamePath), cancellationToken).ConfigureAwait(false);
    }

    private static InputRequest Request(string op, StarRailWindowInfo window, int x = 0, int y = 0) =>
        new(op, window.Handle.ToInt64(), x, y, window.ProcessId);

    private async Task<InputHostClient> GetClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return _client;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _client ??= await InputHostClient.StartAsync(_hostExecutable, cancellationToken).ConfigureAwait(false);
            return _client;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
