using VIvian.App;
using Xunit;

namespace VIvian.Tests.App;

public sealed class AsyncCommandAwaitTests
{
    [Fact]
    public async Task AwaitTracksCompletionAndRejectsDuplicateInvocation()
    {
        var completion = new TaskCompletionSource();
        var calls = 0;
        var command = new AsyncCommand(async () => { calls++; await completion.Task; }, _ => { });
        var pending = command.ExecuteAsync();
        Assert.False(pending.IsCompleted);
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, calls);
        completion.SetResult();
        await pending;
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task FailureUsesExistingErrorHandlerAndReleasesBusyState()
    {
        var failure = new InvalidOperationException("fixture");
        Exception? reported = null;
        var command = new AsyncCommand(() => Task.FromException(failure), ex => reported = ex);
        await command.ExecuteAsync();
        Assert.Same(failure, reported);
        Assert.True(command.CanExecute(null));
    }
}
