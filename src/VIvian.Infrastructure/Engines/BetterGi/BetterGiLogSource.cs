using System.Threading.Channels;
using VIvian.Infrastructure.Logs;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>
/// Where a BetterGI run reads its log lines from. <see cref="Open"/> is called before the engine is launched and must
/// establish the read baseline synchronously: content that existed at that moment is never returned.
/// </summary>
public interface IBetterGiLogSource
{
    IBetterGiLogSubscription Open(string installRoot);
}

/// <summary>Lines appended after <see cref="IBetterGiLogSource.Open"/>, for one consumer. Never contains older content.</summary>
public interface IBetterGiLogSubscription : IAsyncDisposable
{
    IAsyncEnumerable<string> Lines { get; }
}

/// <summary>
/// Reads BetterGI's daily log files with the shared <see cref="LogFileTailer"/>. Both the file of the local start date
/// and the next day's file are tailed from the start (Serilog rolls the file at local midnight), each with its baseline
/// taken before the engine starts, so a run that crosses midnight is still read completely and historical lines are
/// never read. Files are opened read-only with full sharing; nothing is written, moved or deleted. Attribution of the
/// lines to this run happens later (<see cref="BetterGiAttribution"/>); this class does not interpret them.
/// </summary>
public sealed class BetterGiLogFileSource(TimeProvider? time = null, TimeSpan? pollInterval = null) : IBetterGiLogSource
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IBetterGiLogSubscription Open(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        var folder = Path.GetFullPath(Path.Combine(installRoot, BetterGiContract.LogFolder));
        if (Directory.Exists(folder) && ReadOnlyFileAccess.FindReparsePoint(folder) is { } link)
            throw new IOException($"BetterGI 日志目录路径中含有链接（{link}），拒绝读取。");
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var paths = new[] { today, today.AddDays(1) }.Select(d => Path.Combine(folder, BetterGiContract.LogFileName(d))).ToArray();
        return new Subscription(paths, _time, pollInterval);
    }

    private sealed class Subscription : IBetterGiLogSubscription
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<string> _lines = Channel.CreateBounded<string>(
            new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly Task _pumps;

        public Subscription(IReadOnlyList<string> paths, TimeProvider time, TimeSpan? pollInterval)
        {
            var pumps = new List<Task>();
            foreach (var path in paths)
            {
                var enumerator = new LogFileTailer(path, time, pollInterval).ReadLinesAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
                // The first MoveNextAsync runs synchronously up to the tailer's first await, which records the
                // current file length as the baseline. It therefore happens here, before the engine is launched.
                var first = enumerator.MoveNextAsync();
                pumps.Add(PumpAsync(enumerator, first));
            }
            _pumps = Task.WhenAll(pumps);
        }

        public IAsyncEnumerable<string> Lines => _lines.Reader.ReadAllAsync();

        private async Task PumpAsync(IAsyncEnumerator<string> enumerator, ValueTask<bool> first)
        {
            try
            {
                var next = first;
                while (await next.ConfigureAwait(false))
                {
                    await _lines.Writer.WriteAsync(enumerator.Current, _cts.Token).ConfigureAwait(false);
                    next = enumerator.MoveNextAsync();
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A log that cannot be read carries no evidence; the run's verdict then stays Indeterminate.
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await _pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* tailers only read; nothing to clean up */ }
            _lines.Writer.TryComplete();
            _cts.Dispose();
        }
    }
}
