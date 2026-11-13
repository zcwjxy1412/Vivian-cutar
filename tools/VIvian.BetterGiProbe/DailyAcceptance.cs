using VIvian.Infrastructure.Engines.BetterGi;

internal static class DailyAcceptance
{
    public static async Task<int> RunAsync(string stage, Guid? accountId = null)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += interrupt;
        try
        {
            Func<string, string>? verify = accountId is { } id ? path => PreparedAccountPlan.Verify(path, id) : null;
            var result = await BetterGiDailyAcceptance.RunLegacyAsync(stage, verify, Console.WriteLine, stop.Token);
            return result.DiagnosticPassed ? 0 : 1;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 1; }
        finally { Console.CancelKeyPress -= interrupt; }
    }

    public static async Task<int> RecoverAsync(string stage)
    {
        var result = await BetterGiDailyAcceptance.RecoverAsync(stage);
        Console.WriteLine("已核对协议和原配置，并解除验收桌面锁。");
        return result;
    }
}
