using System.Reflection;

using Microsoft.AspNetCore.Hosting.Server;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// Keeps Kestrel's timers running while the public listener drains (docs/design/hosting.md "Shutdown"). <c>KestrelServerImpl.StopAsync</c>
/// disposes the heartbeat before anything else, and the heartbeat is what ticks every connection's timeout control: the request-body
/// and response data rates, the headers timeout, the keep-alive timeout. A client trickling an upload at 10 B/s, cut in 11 s while the server
/// runs, was never cut once the stop began, so it held the drain for its whole budget.
/// </summary>
/// <remarks>
/// <para>It calls the same <c>Heartbeat.OnHeartbeat()</c> Kestrel's timer thread does, once a second, only while that thread is gone: two callers
/// at once would tick a connection's timeout control from two threads.</para>
/// <para><b>Not available</b> (<see cref="TryCreate"/> returns null) when the server isn't Kestrel or it has moved the members this reaches;
/// the stop then behaves as it did, and the drain's budget ends a slow upload.</para>
/// </remarks>
public sealed class KestrelDrainHeartbeat
{
    // Kestrel's own interval (Heartbeat.Interval): a data-rate cut lands within a second of the rule being broken, as it does while running.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Action _tick;
    private readonly Thread _kestrelsTimer;

    private KestrelDrainHeartbeat(Action tick, Thread kestrelsTimer)
    {
        _tick = tick;
        _kestrelsTimer = kestrelsTimer;
    }

    /// <summary>The number of ticks this has made; for the tests.</summary>
    public long Ticks { get; private set; }

    public static KestrelDrainHeartbeat? TryCreate(IServer? server)
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // The public host registers Kestrel's implementation as the IServer; the KestrelServer wrapper holds one in a field.
        var name = server?.GetType().FullName;
        var inner = name switch
        {
            "Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl" => server,
            "Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServer" => server!.GetType().GetField("_innerKestrelServer", Instance)?.GetValue(server),
            _ => null,
        };
        var heartbeat = inner?.GetType().GetProperty("ServiceContext", Instance)?.GetValue(inner) is { } context
            ? context.GetType().GetProperty("Heartbeat", Instance)?.GetValue(context)
            : null;
        if (heartbeat is null
            || heartbeat.GetType().GetField("_timerThread", Instance)?.GetValue(heartbeat) is not Thread timer
            || heartbeat.GetType().GetMethod("OnHeartbeat", Instance, Type.EmptyTypes)?.CreateDelegate<Action>(heartbeat) is not { } tick)
        {
            return null;
        }

        return new KestrelDrainHeartbeat(tick, timer);
    }

    /// <summary>Ticks until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Kestrel's thread is joined by its Dispose, at the very start of StopAsync; until then it is the one ticking.
                if (!_kestrelsTimer.IsAlive)
                {
                    _tick();
                    Ticks++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The public host has stopped.
        }
    }
}
