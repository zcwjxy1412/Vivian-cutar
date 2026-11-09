namespace VIvian.Infrastructure.Accounts;

/// <summary>
/// Moves VIvian's own windows out of the way while the game is captured, so a normal-integrity VIvian can read an
/// elevated game window without raising it. Disposing the returned handle restores VIvian exactly as it was.
/// </summary>
public interface IScanWindowShield
{
    Task<IAsyncDisposable> HideAsync(CancellationToken cancellationToken);
}

public sealed class NoScanWindowShield : IScanWindowShield
{
    public static NoScanWindowShield Instance { get; } = new();
    public Task<IAsyncDisposable> HideAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IAsyncDisposable>(new Nothing());

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
