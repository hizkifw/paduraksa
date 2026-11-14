using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Model;

using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>What YARP currently believes about one side of a destination's health.</summary>
public enum DestinationVerdict
{
    /// <summary>No verdict yet: a new destination, or one whose passive verdict lapsed. YARP routes to it.</summary>
    Unknown,
    Healthy,
    Unhealthy,
}

/// <summary>The outcome of the last active probe of a destination, in words for a person.</summary>
/// <param name="At">When the probe finished.</param>
/// <param name="Succeeded">A 2xx-3xx answer is what YARP's default policy counts as a pass.</param>
/// <param name="Reason">"HTTP 503", "timed out", "connection refused", ...; for a pass, the status.</param>
public sealed record ProbeOutcome(DateTimeOffset At, bool Succeeded, string Reason);

/// <summary>One destination of the live proxy as its health stands.</summary>
/// <param name="Id">The destination.</param>
/// <param name="Address">The address it is configured with.</param>
/// <param name="Active">The active probe policy's verdict.</param>
/// <param name="Passive">The passive (failure rate) policy's verdict.</param>
/// <param name="Current">YARP's combined verdict: unhealthy when either side is.</param>
/// <param name="LastProbe">The last active probe, if probing is on and one has finished.</param>
public sealed record DestinationReading(
    DestinationId Id, string Address, DestinationVerdict Active, DestinationVerdict Passive, DestinationVerdict Current, ProbeOutcome? LastProbe);

/// <summary>An upstream of the live proxy and its destinations' health.</summary>
/// <param name="Id">The upstream (YARP's cluster ID).</param>
/// <param name="ActiveChecks">Active probing is configured.</param>
/// <param name="PassiveChecks">Passive checking is configured.</param>
/// <param name="Destinations">Every destination the proxy knows of.</param>
public sealed record UpstreamReading(UpstreamId Id, bool ActiveChecks, bool PassiveChecks, IReadOnlyList<DestinationReading> Destinations)
{
    /// <summary>Whether any health check is configured; without one every destination stays Unknown and the upstream is never "down".</summary>
    public bool HasChecks => ActiveChecks || PassiveChecks;

    /// <summary>
    /// The upstream has destinations and none is routable. YARP itself would still try them all in that case, which is why this
    /// reads each destination's verdict and not YARP's list of available destinations.
    /// </summary>
    public bool NoneAvailable
    {
        get
        {
            foreach (var destination in Destinations)
            {
                if (destination.Current != DestinationVerdict.Unhealthy)
                {
                    return false;
                }
            }

            return Destinations.Count > 0;
        }
    }
}

/// <summary>
/// Remembers each destination's last active probe, written from the probe loop (a timer, never a request) by <see cref="ProbeRecordingPolicy"/>.
/// Held weakly by destination state, so a removed destination takes its record with it.
/// </summary>
public sealed class ProbeLog(TimeProvider? time = null)
{
    private readonly ConditionalWeakTable<DestinationState, ProbeOutcome> _last = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    internal void Record(DestinationState destination, HttpResponseMessage? response, Exception? failure)
    {
        var (succeeded, reason) = Describe(response, failure);
        _last.AddOrUpdate(destination, new ProbeOutcome(_time.GetUtcNow(), succeeded, reason));
    }

    internal ProbeOutcome? Last(DestinationState destination) => _last.TryGetValue(destination, out var outcome) ? outcome : null;

    /// <summary>Words for a probe's result. Never the exception's message: it can carry an address or a path.</summary>
    internal static (bool Succeeded, string Reason) Describe(HttpResponseMessage? response, Exception? failure)
    {
        if (failure is null && response is not null)
        {
            var code = (int)response.StatusCode;
            return (code is >= 200 and < 400, $"HTTP {code}");
        }

        return (false, failure switch
        {
            TaskCanceledException or OperationCanceledException or TimeoutException => "timed out",
            HttpRequestException { InnerException: SocketException socket } => SocketReason(socket.SocketErrorCode),
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "name not found",
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "TLS handshake failed",
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "connection failed",
            HttpRequestException => "request failed",
            null => "no answer",
            _ => "probe failed",
        });
    }

    private static string SocketReason(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.HostNotFound or SocketError.NoData => "name not found",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "host unreachable",
        SocketError.TimedOut => "timed out",
        SocketError.ConnectionReset => "connection reset",
        _ => "connection failed",
    };
}

/// <summary>
/// YARP's own active policy with a recorder in front of it: the verdict logic is YARP's (it is not public, so it is
/// built from its registration), this notes what each probe said, which YARP does not keep, and tells the passive policy when a
/// destination the probes had down answers again (<see cref="PassiveFailureRatePolicy.ActiveRecovered"/>).
/// </summary>
internal sealed class ProbeRecordingPolicy(IActiveHealthCheckPolicy inner, ProbeLog log, PassiveFailureRatePolicy? passive = null) : IActiveHealthCheckPolicy
{
    public string Name => inner.Name;

    public void ProbingCompleted(ClusterState cluster, IReadOnlyList<DestinationProbingResult> probingResults)
    {
        // The monitor disposes each response once this returns, so what is wanted is read first.
        var wasDown = new bool[probingResults.Count];
        for (var i = 0; i < probingResults.Count; i++)
        {
            var result = probingResults[i];
            log.Record(result.Destination, result.Response, result.Exception);
            wasDown[i] = result.Destination.Health.Active == DestinationHealth.Unhealthy;
        }

        // YARP's policy sets the new verdicts before it returns.
        inner.ProbingCompleted(cluster, probingResults);
        if (passive is null)
        {
            return;
        }

        for (var i = 0; i < probingResults.Count; i++)
        {
            var destination = probingResults[i].Destination;
            if (wasDown[i] && destination.Health.Active != DestinationHealth.Unhealthy)
            {
                passive.ActiveRecovered(cluster, destination);
            }
        }
    }
}

internal static class ProbeRecording
{
    /// <summary>Wraps YARP's registered active policies so each probe is recorded; call after <c>AddReverseProxy()</c>.</summary>
    public static IServiceCollection RecordActiveProbes(this IServiceCollection services)
    {
        services.AddSingleton<ProbeLog>(static provider => new ProbeLog(provider.GetService<TimeProvider>()));
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IActiveHealthCheckPolicy) || descriptor.ImplementationType is not { } implementation)
            {
                continue;
            }

            services[i] = ServiceDescriptor.Singleton<IActiveHealthCheckPolicy>(provider => new ProbeRecordingPolicy(
                (IActiveHealthCheckPolicy)ActivatorUtilities.CreateInstance(provider, implementation),
                provider.GetRequiredService<ProbeLog>(),
                provider.GetService<PassiveFailureRatePolicy>()));
        }

        return services;
    }
}
