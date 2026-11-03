using Microsoft.Extensions.Time.Testing;
using VIvian.Infrastructure.Logs;
using Xunit;

namespace VIvian.Tests.Logs;

public sealed class LogFileTailerTests
{
    [Fact]
    public async Task ExistingLinesAreIgnoredButNewLinesAreRead()
    {
        using var test = new LogTest();
        File.WriteAllText(test.Path, "old\n");
        await using var tail = test.Start();
        await tail.TickWithoutLineAsync();
        File.AppendAllText(test.Path, "new\n");
        Assert.Equal("new", await tail.NextAsync());
    }

    [Fact]
    public async Task MissingFileAndDeletionAreHandled()
    {
        using var test = new LogTest();
        await using var tail = test.Start();
        await tail.TickWithoutLineAsync();
        File.WriteAllText(test.Path, "created\n");
        Assert.Equal("created", await tail.NextAsync());
        File.Delete(test.Path);
        await tail.TickWithoutLineAsync();
        File.WriteAllText(test.Path, "recreated\n");
        Assert.Equal("recreated", await tail.NextAsync());
    }

    [Fact]
    public async Task RotationReadsNewFileFromBeginningAndDropsOldPartialLine()
    {
        using var test = new LogTest();
        File.WriteAllText(test.Path, "old\n");
        await using var tail = test.Start();
        File.AppendAllText(test.Path, "incomplete");
        await tail.TickWithoutLineAsync();
        File.Move(test.Path, test.Path + ".1");
        File.WriteAllText(test.Path, "replacement\n");
        Assert.Equal("replacement", await tail.NextAsync());
    }

    [Fact]
    public async Task TruncationResetsOffsetAndPartialLine()
    {
        using var test = new LogTest();
        File.WriteAllText(test.Path, "historical-long-line\n");
        await using var tail = test.Start();
        File.AppendAllText(test.Path, "partial");
        await tail.TickWithoutLineAsync();
        File.WriteAllText(test.Path, "");
        await tail.TickWithoutLineAsync();
        File.AppendAllText(test.Path, "fresh\n");
        Assert.Equal("fresh", await tail.NextAsync());
    }

    [Fact]
    public async Task PartialUtf8AndBomWaitForCompleteLine()
    {
        using var test = new LogTest();
        await using var tail = test.Start();
        File.WriteAllBytes(test.Path, [0xEF, 0xBB, 0xBF, 0xE4, 0xB8]);
        await tail.TickWithoutLineAsync();
        using (var writer = new FileStream(test.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            writer.Write([0xAD, (byte)'\r', (byte)'\n']);
        Assert.Equal("中", await tail.NextAsync());
    }

    [Fact]
    public async Task MultipleLinesAreEmittedInOrderWithoutRepeating()
    {
        using var test = new LogTest();
        await using var tail = test.Start();
        File.WriteAllText(test.Path, "一\n二\n");
        Assert.Equal("一", await tail.NextAsync());
        Assert.Equal("二", await tail.NextAsync());
        await tail.TickWithoutLineAsync();
        File.AppendAllText(test.Path, "三\n");
        Assert.Equal("三", await tail.NextAsync());
    }

    [Fact]
    public async Task CancellationInterruptsVirtualClockWait()
    {
        using var test = new LogTest();
        await using var tail = test.Start();
        tail.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tail.Pending);
    }

    private sealed class LogTest : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vivian-tail-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "engine.log");

        public LogTest() => Directory.CreateDirectory(_directory);
        public TailSession Start() => new(Path);
        public void Dispose() => TestDirectories.DeleteWithRetry(_directory);
    }

    private sealed class TailSession : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly IAsyncEnumerator<string> _reader;
        public Task<bool> Pending { get; private set; }

        public TailSession(string path)
        {
            _reader = new LogFileTailer(path, _time, TimeSpan.FromMilliseconds(100))
                .ReadLinesAsync(_stop.Token).GetAsyncEnumerator();
            Pending = _reader.MoveNextAsync().AsTask(); // baseline established here
        }

        public async Task<string> NextAsync()
        {
            for (var i = 0; i < 100 && !Pending.IsCompleted; i++)
            {
                _time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(2);
            }
            Assert.True(Pending.IsCompleted, "tailer did not emit a line after 100 virtual polls");
            Assert.True(await Pending);
            var line = _reader.Current;
            Pending = _reader.MoveNextAsync().AsTask();
            return line;
        }

        public async Task TickWithoutLineAsync()
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
            Assert.False(Pending.IsCompleted, "unexpected line or failure while polling");
        }

        public void Cancel() => _stop.Cancel();

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await Pending; }
            catch (OperationCanceledException) { }
            await _reader.DisposeAsync();
            _stop.Dispose();
        }
    }
}
