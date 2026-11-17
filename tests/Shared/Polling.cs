// Linked into the test projects that wait for background work (<Compile Include Link>), so it compiles into each assembly as an
// internal type of its own.
using System.Diagnostics;

namespace Paduraksa.Tests.Shared;

internal static class Polling
{
    /// <summary>
    /// Polls <paramref name="condition"/> every <paramref name="poll"/> until it holds, and fails with <paramref name="what"/> once
    /// <paramref name="timeout"/> has passed. Each caller picks its own bound: a loaded machine takes as long as it takes, and a wait
    /// that is too short is a flake.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan poll, string what, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < timeout, what);
            await Task.Delay(poll, cancellationToken);
        }
    }
}
