using System.Text;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Tests.Processes;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

/// <summary>
/// Uses the shared LogFileTailer, whose file identity is reliable only on Windows (see LogFileTailerTests), so these
/// run on Windows CI and are reported as skipped elsewhere.
/// </summary>
public sealed class BetterGiLogFileSourceTests
{
    [WindowsFact]
    public async Task ContentWrittenBeforeOpenIsNeverReadAndNewLinesAre()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivian-bgi-log-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "log");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, BetterGiContract.LogFileName(DateOnly.FromDateTime(DateTime.Now)));
        try
        {
            await File.WriteAllTextAsync(file, "old line from an earlier run\n", new UTF8Encoding(false));
            await using var sub = new BetterGiLogFileSource(pollInterval: TimeSpan.FromMilliseconds(20)).Open(root);
            await File.AppendAllTextAsync(file, "new line\n", new UTF8Encoding(false));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var line in sub.Lines.WithCancellation(cts.Token))
            {
                Assert.Equal("new line", line);
                break;
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OpeningWithoutALogFolderDoesNotCreateOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivian-bgi-nolog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sub = new BetterGiLogFileSource(pollInterval: TimeSpan.FromMilliseconds(20)).Open(root);
            await sub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Directory.Exists(Path.Combine(root, "log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
