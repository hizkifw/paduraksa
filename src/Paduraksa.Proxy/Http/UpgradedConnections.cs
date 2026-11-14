using System.Collections.Concurrent;

using Microsoft.AspNetCore.Http;

namespace Paduraksa.Proxy.Http;

/// <summary>
/// The public app's open upgraded connections (WebSockets over HTTP/1.1 upgrade or HTTP/2 extended <c>CONNECT</c>, and any other
/// upgrade YARP forwards), so a graceful stop can end them at once instead of waiting out its whole drain for connections that never
/// end on their own (docs/design/hosting.md "Shutdown").
/// </summary>
/// <remarks>
/// A WebSocket gets a close frame, 1001 Going Away, written between two of the upstream's frames, and then a short while for the
/// closing handshake to run through to the upstream and back; anything else, or a WebSocket still open after that, is aborted.
/// Everything here runs once per upgrade or at the stop, never per message or per ordinary request.
/// </remarks>
public sealed class UpgradedConnections
{
    /// <summary>How long a close frame may wait for the upstream to finish the frame it is sending.</summary>
    internal static readonly TimeSpan FrameBoundaryWait = TimeSpan.FromSeconds(1);

    /// <summary>How long after its close frame a WebSocket has to finish the closing handshake before it is aborted.</summary>
    internal static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<UpgradedStream, byte> _open = new();
    private volatile bool _closing;

    /// <summary>How many upgraded connections are open now.</summary>
    public int Count => _open.Count;

    /// <summary>
    /// Ends every open upgraded connection and every one opened from now on, and completes once all have ended. Cancelling
    /// <paramref name="cancellationToken"/> aborts those still open and returns.
    /// </summary>
    public async Task CloseAllAsync(CancellationToken cancellationToken)
    {
        _closing = true;
        List<Task> closing = [];
        foreach (var connection in _open.Keys)
        {
            closing.Add(connection.CloseAsync(cancellationToken));
        }

        await Task.WhenAll(closing).ConfigureAwait(false);
    }

    /// <summary>Wraps the client's side of an upgrade YARP is about to use, and tracks it until the request ends.</summary>
    internal Stream Track(HttpContext context, Stream client, bool webSocket)
    {
        UpgradedStream stream = new(this, context, client, webSocket);
        _open.TryAdd(stream, 0);
        context.Response.OnCompleted(static state =>
        {
            ((UpgradedStream)state).Ended();
            return Task.CompletedTask;
        }, stream);

        // Accepted while the stop was already closing the others: the drain must not wait for this one either.
        if (_closing)
        {
            _ = stream.CloseAsync(CancellationToken.None);
        }

        return stream;
    }

    internal void Remove(UpgradedStream stream) => _open.TryRemove(stream, out _);
}
