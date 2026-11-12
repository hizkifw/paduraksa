using System.Collections.Concurrent;
using System.Diagnostics;

namespace Paduraksa.Mcp;

/// <summary>
/// Starts every child from one thread that lives as long as the pool. The child asks the kernel to kill it when its parent dies
/// (<c>PR_SET_PDEATHSIG</c>), and Linux means the parent <i>thread</i> that started it: started from a thread-pool thread, a child would be killed whenever
/// the pool retired that thread.
/// </summary>
internal sealed class ChildSpawner : IDisposable
{
    private readonly BlockingCollection<(ProcessStartInfo Info, TaskCompletionSource<Process> Done)> _requests = [];
    private readonly Thread _thread;

    public ChildSpawner()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "paduraksa-mcp-spawner" };
        _thread.Start();
    }

    public Task<Process> StartAsync(ProcessStartInfo info)
    {
        TaskCompletionSource<Process> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add((info, done));
        return done.Task;
    }

    public void Dispose() => _requests.CompleteAdding();

    private void Loop()
    {
        foreach (var (info, done) in _requests.GetConsumingEnumerable())
        {
            try
            {
                done.SetResult(Process.Start(info) ?? throw new InvalidOperationException("The child did not start."));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }
    }
}
