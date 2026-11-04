namespace VIvian.Core.Execution;

internal static class TaskObservation
{
    /// <summary>
    /// Marks a task whose bounded wait gave up as observed, so a fault that arrives later is never left
    /// unobserved. The caller has already decided the outcome (never "cleaned up") when it stopped waiting.
    /// </summary>
    public static void ObserveLater(this Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
