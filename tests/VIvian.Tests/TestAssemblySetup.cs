using System.Runtime.CompilerServices;

namespace VIvian.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// Many tests block thread-pool threads on purpose (adapters that ignore cancellation, launches held at a gate),
    /// and xUnit runs test classes in parallel. On a 2-core CI runner the pool then injects threads slowly, and an
    /// unrelated continuation can wait seconds for a thread, which turns real-time safety budgets into flaky
    /// failures (Windows runs 41891143853, 41892014390). A higher minimum gives every test its threads at once.
    /// Product code is unaffected: this runs only in the test process.
    /// </summary>
#pragma warning disable CA2255 // intentional: a test assembly configures its own process
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
    }
}
