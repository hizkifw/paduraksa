using System.Runtime.CompilerServices;

using Bunit;

namespace Paduraksa.Tests.Dashboard;

internal static class TestAssemblySetup
{
    // CI runs the suite on 4 cores with the test classes in parallel. bUnit's 1 s default wait then expires before the renderer's
    // dispatcher has even run the check (its own diagnosis: a starved thread pool), so waits get room and the pool a head start.
    // A wait still ends as soon as its condition holds, so a passing test is no slower.
    [ModuleInitializer]
    internal static void Initialize()
    {
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), io);
    }
}
