namespace VIvian.Infrastructure.Engines.OneDragon;

/// <summary>Serializes writes even after a timeout; a late non-cooperative writer never races a terminal write.</summary>
internal sealed class OneDragonJournalBoundary(TimeSpan timeout)
{
    private readonly SemaphoreSlim _serial = new(1, 1);

    internal async Task WriteAsync(Func<CancellationToken, Task> write, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stop = new CancellationTokenSource();
        // Invoke off the safety path: an implementation may block before returning its Task.
        var operation = Task.Run(async () =>
        {
            await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                await write(stop.Token).ConfigureAwait(false);
            }
            finally { _serial.Release(); }
        });
        Task cancellation = Task.CompletedTask;
        try { await operation.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch
        {
            // Cancellation callbacks are untrusted too; never wait for them on the cleanup path.
            cancellation = stop.CancelAsync();
            throw;
        }
        finally { _ = ObserveAsync(operation, cancellation, stop); }
    }

    private static async Task ObserveAsync(Task operation, Task cancellation, CancellationTokenSource stop)
    {
        try { await Task.WhenAll(operation, cancellation).ConfigureAwait(false); }
        catch (Exception) { /* The bounded caller owns the failure; observe late completions without changing state. */ }
        finally { stop.Dispose(); }
    }
}
