using System.Collections.Concurrent;
using System.Text;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>A loopback server that records what is POSTed to it (and ignores the Node prober's <c>GET /</c> that this machine sends to new ports).</summary>
internal sealed class RecordingHook : IAsyncDisposable
{
    private readonly LoopbackUpstream _upstream;
    private readonly ConcurrentQueue<(string Path, string Body, string? Signature, string? EventId)> _received = new();

    private RecordingHook(LoopbackUpstream upstream)
    {
        _upstream = upstream;
        Url = upstream.Address.TrimEnd('/');
    }

    public string Url { get; }

    /// <summary>The status POSTs are answered with (200 unless a test changes it).</summary>
    public int Status { get; set; } = 200;

    public IReadOnlyList<(string Path, string Body, string? Signature, string? EventId)> Received => [.. _received];

    public static async Task<RecordingHook> StartAsync()
    {
        RecordingHook? hook = null;
        var upstream = await LoopbackUpstream.StartAsync(
            async context =>
            {
                if (context.Request.Method != "POST")
                {
                    return;
                }

                context.Response.StatusCode = hook!.Status;
                using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
                hook!._received.Enqueue((context.Request.Path.Value!, await reader.ReadToEndAsync(), context.Request.Headers["X-Paduraksa-Signature"].ToString() is { Length: > 0 } sig ? sig : null, context.Request.Headers["X-Paduraksa-Event-Id"].ToString() is { Length: > 0 } id ? id : null));
            },
            cancellationToken: TestContext.Current.CancellationToken);
        hook = new RecordingHook(upstream);
        return hook;
    }

    public ValueTask DisposeAsync() => _upstream.DisposeAsync();
}
