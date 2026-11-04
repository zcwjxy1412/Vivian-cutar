using System.Diagnostics;
using VIvian.Core.Execution;

namespace VIvian.Tests.Execution;

internal static class VirtualClock
{
    /// <summary>
    /// Waits (real time, bounded) until the scheduler has no healthy state write in flight: either nothing is queued
    /// beyond the writes the test stalls on purpose, or the journal is already known to be stalled (then later writes
    /// queue behind it and moving time on is exactly what the test wants).
    /// </summary>
    public static async Task WaitForHealthyWritesAsync(QueueScheduler scheduler, Func<int>? deliberatelyStalled = null)
    {
        var clock = Stopwatch.StartNew();
        while (!scheduler.JournalStalled && scheduler.StateWritesInFlight > (deliberatelyStalled?.Invoke() ?? 0))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("a healthy journal write did not finish within 30 real seconds");
            await Task.Delay(1);
        }
    }
}
