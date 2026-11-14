using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// Where one traced request spent its time inside the .NET client, from the <c>System.Net.Http</c> events: so a request that hangs says whether it waited for a
/// connection, whether its headers were ever written, on which connection, and whether that connection closed.
/// </summary>
internal sealed class RequestTrace
{
    public long Started = Stopwatch.GetTimestamp();
    public double QueueMs = -1;
    public long HeadersWritten;
    public long ResponseHeadersStarted;
    public long ResponseHeadersRead;
    public long ConnectionId = -1;

    public string Describe(ClientTrace connections)
    {
        StringBuilder text = new("client:");
        if (QueueMs >= 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" queued {QueueMs:F0} ms,");
        }

        text.Append(HeadersWritten == 0 ? " headers never written" : $" headers written {Since(HeadersWritten)}");
        if (ConnectionId >= 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" on connection {ConnectionId} ({connections.Describe(ConnectionId, Started)})");
        }

        text.Append(ResponseHeadersStarted == 0 ? ", no response headers awaited" : $", awaiting response headers {Since(ResponseHeadersStarted)}");
        text.Append(ResponseHeadersRead == 0 ? ", none read" : $", read {Since(ResponseHeadersRead)}");
        return text.ToString();
    }

    public string Since(long timestamp) => At(Started, timestamp);

    public static string At(long origin, long timestamp) =>
        string.Create(CultureInfo.InvariantCulture, $"{(timestamp - origin) / (double)Stopwatch.Frequency:+0.000;-0.000} s");
}

/// <summary>
/// Listens to the client's <c>System.Net.Http</c> events. A request's own events are raised on its async flow, so <see cref="Current"/> ties them to it; connection
/// events are kept for every connection, briefly, so a hung request's connection can be described.
/// </summary>
internal sealed class ClientTrace : EventListener
{
    public static readonly AsyncLocal<RequestTrace?> Current = new();

    private readonly ConcurrentDictionary<long, (long Opened, long Closed, int Version)> _connections = new();

    public string Describe(long connectionId, long origin) => _connections.TryGetValue(connectionId, out var c)
        ? $"h{c.Version}, opened {RequestTrace.At(origin, c.Opened)}" + (c.Closed == 0 ? ", still open" : $", closed {RequestTrace.At(origin, c.Closed)}")
        : "not seen opening";

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "System.Net.Http")
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        switch (e.EventName)
        {
            case "ConnectionEstablished" when e.Payload is { Count: >= 3 } p:
                _connections[Convert.ToInt64(p[2], CultureInfo.InvariantCulture)] = (now, 0, Convert.ToInt32(p[0], CultureInfo.InvariantCulture));
                Prune(now);
                return;
            case "ConnectionClosed" when e.Payload is { Count: >= 3 } p:
                var id = Convert.ToInt64(p[2], CultureInfo.InvariantCulture);
                if (_connections.TryGetValue(id, out var known))
                {
                    _connections[id] = known with { Closed = now };
                }

                return;
        }

        if (Current.Value is not { } trace)
        {
            return;
        }

        switch (e.EventName)
        {
            case "RequestLeftQueue" when e.Payload is { Count: >= 1 } p:
                trace.QueueMs = Convert.ToDouble(p[0], CultureInfo.InvariantCulture);
                break;
            case "RequestHeadersStart":
                trace.HeadersWritten = now;
                if (e.Payload is { Count: >= 1 } headers && headers[0] is long connection)
                {
                    trace.ConnectionId = connection;
                }

                break;
            case "ResponseHeadersStart":
                trace.ResponseHeadersStarted = now;
                break;
            case "ResponseHeadersStop":
                trace.ResponseHeadersRead = now;
                break;
        }
    }

    // Closed connections are kept ten minutes, longer than any request waits.
    private void Prune(long now)
    {
        if (_connections.Count < 4096)
        {
            return;
        }

        foreach (var (id, c) in _connections)
        {
            if (c.Closed != 0 && now - c.Closed > Stopwatch.Frequency * 600)
            {
                _connections.TryRemove(id, out _);
            }
        }
    }
}
