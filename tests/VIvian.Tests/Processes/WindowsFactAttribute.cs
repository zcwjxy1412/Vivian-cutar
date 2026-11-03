using Xunit;

namespace VIvian.Tests.Processes;

/// <summary>Runs only on Windows (job objects, real processes). Reported as skipped elsewhere — never as passed.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "需要 Windows：真实进程与作业对象测试，仅在 Windows CI 上执行。";
    }
}

/// <summary>Tests that start FakeEngine processes run one at a time: another test's engine would look like a
/// foreign process to preflight, and every test must leave no process behind.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCollection
{
    public const string Name = "Real process tests";
}
