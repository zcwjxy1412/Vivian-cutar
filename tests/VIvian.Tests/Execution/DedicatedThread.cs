namespace VIvian.Tests.Execution;

/// <summary>
/// Runs work that blocks on purpose on its own thread, never on the thread pool: several test classes run in
/// parallel and block pool threads deliberately, so a pool work item can wait seconds for a thread (starvation).
/// </summary>
internal static class DedicatedThread
{
    public static Task Run(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { action(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "test: deliberately blocking" }.Start();
        return done.Task;
    }

    public static Task<T> Run<T>(Func<Task<T>> start)
    {
        var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { started.TrySetResult(start()); }
            catch (Exception ex) { started.TrySetException(ex); }
        }) { IsBackground = true, Name = "test: deliberately blocking" }.Start();
        return started.Task.Unwrap();
    }
}
