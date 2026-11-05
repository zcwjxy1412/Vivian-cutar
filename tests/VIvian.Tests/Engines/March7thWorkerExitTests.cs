using VIvian.Infrastructure.Engines.Cli;
using System.Diagnostics;
using Xunit;

namespace VIvian.Tests.Engines;

public class March7thWorkerExitTests
{
    private const string Id = "20261031-123456-ab12";
    private const string Record = """
        {"run_id":"20261031-123456-ab12","task":"daily","ended_at":"2026-10-31T12:40:00","lock":"acquired","pid":1234,"pid_create_time":1793450000.5,"status":"stopped"}
        """;

    [Fact]
    public void ConfirmsOnlyExactFinishedWorkerIdentity()
    {
        Assert.Equal(WorkerExitState.Exited, March7thWorkerExit.Check(Record, Id, "daily", _ => (false, null)));
        Assert.Equal(WorkerExitState.Running, March7thWorkerExit.Check(Record, Id, "daily", _ => (true, 1793450000.5)));
        Assert.Equal(WorkerExitState.Exited, March7thWorkerExit.Check(Record, Id, "daily", _ => (true, 1793459000.5)));
        Assert.Equal(WorkerExitState.Unknown, March7thWorkerExit.Check(Record, Id, "daily", _ => (true, null)));
        Assert.Equal(WorkerExitState.Unknown, March7thWorkerExit.Check(Record, "20261031-123456-ffff", "daily", _ => (false, null)));
        Assert.Equal(WorkerExitState.Unknown, March7thWorkerExit.Check(Record, Id, "power", _ => (false, null)));
        Assert.Equal(WorkerExitState.Unknown, March7thWorkerExit.Check(Record.Replace("\"stopped\"", "\"running\""), Id, "daily", _ => (false, null)));
    }

    [Fact]
    public void WindowsObserverRecognizesTheCurrentLiveProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0;
        var record = $$"""{"run_id":"{{Id}}","task":"daily","ended_at":"synthetic","lock":"acquired","pid":{{process.Id}},"pid_create_time":{{started.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"status":"stopped"}""";
        Assert.Equal(WorkerExitState.Running, March7thWorkerExit.Check(record, Id, "daily"));
    }

    [Fact]
    public async Task WindowsObserverDistinguishesAnOwnedHarmlessProcessBeforeAndAfterExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 30")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0;
            var record = $$"""{"run_id":"{{Id}}","task":"daily","ended_at":"synthetic","lock":"acquired","pid":{{process.Id}},"pid_create_time":{{started.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"status":"stopped"}""";
            Assert.Equal(WorkerExitState.Running, March7thWorkerExit.Check(record, Id, "daily"));
            process.Kill(); // Exact handle created by this test; never search or kill by name.
            await process.WaitForExitAsync();
            Assert.Equal(WorkerExitState.Exited, March7thWorkerExit.Check(record, Id, "daily"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
}
