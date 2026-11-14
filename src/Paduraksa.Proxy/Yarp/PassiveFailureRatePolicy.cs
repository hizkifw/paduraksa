using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// The passive health policy, in place of YARP's <c>TransportFailureRate</c> and under its name: the same verdict (the share of
/// transport failures over <see cref="TransportFailureRateHealthPolicyOptions.DetectionWindowSize"/>, once
/// <see cref="TransportFailureRateHealthPolicyOptions.MinimalTotalCountThreshold"/> requests have been seen, against
/// <see cref="TransportFailureRateHealthPolicyOptions.DefaultFailureRateLimit"/>), with the two differences docs/design/observability.md
/// "Passive health" gives the reasons for:
/// <list type="bullet">
/// <item>A destination it takes out stays out for the upstream's own reactivation period. YARP's raises any shorter period to the
/// detection window (60 s), because its history would otherwise take the destination out again on its first request back.</item>
/// <item>Instead, the history is cleared when it takes a destination out and when the active check sees one come back, and a request
/// that ends while the destination is out (it was in flight) is not recorded.</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Request path.</b> 0 B and no lock: a destination's window is a fixed array of one-second buckets, each one <c>long</c> holding its
/// second and its two counts, updated by compare-and-swap. Only a failure sums the window, and only a change of verdict calls YARP's
/// updater (which allocates to refresh the cluster's destinations).
/// </remarks>
internal sealed class PassiveFailureRatePolicy : IPassiveHealthCheckPolicy
{
    private static readonly TimeSpan DefaultReactivationPeriod = TimeSpan.FromSeconds(60);

    private readonly IDestinationHealthUpdater _updater;
    private readonly TimeProvider _time;
    private readonly int _windowSeconds;
    private readonly int _minimalTotal;
    private readonly double _failureRateLimit;
    private readonly ConditionalWeakTable<DestinationState, FailureWindow> _windows = [];
    private readonly ConditionalWeakTable<DestinationState, FailureWindow>.CreateValueCallback _create;

    public PassiveFailureRatePolicy(IOptions<TransportFailureRateHealthPolicyOptions> options, TimeProvider time, IDestinationHealthUpdater updater)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(updater);
        var settings = options.Value;
        _updater = updater;
        _time = time;
        _windowSeconds = (int)Math.Clamp(Math.Ceiling(settings.DetectionWindowSize.TotalSeconds), 1, FailureWindow.MaxSeconds);
        _minimalTotal = settings.MinimalTotalCountThreshold;
        _failureRateLimit = settings.DefaultFailureRateLimit;
        var seconds = _windowSeconds;
        _create = _ => new FailureWindow(seconds);
    }

    public string Name => HealthCheckConstants.PassivePolicy.TransportFailureRate;

    public void RequestProxied(HttpContext context, ClusterState cluster, DestinationState destination)
    {
        var health = destination.Health;
        if (health.Passive == DestinationHealth.Unhealthy)
        {
            return;
        }

        var window = _windows.GetValue(destination, _create);
        var now = _time.GetTimestamp() / _time.TimestampFrequency;
        if (!Failed(context))
        {
            window.Record(now, failed: false);
            if (health.Passive == DestinationHealth.Unknown)
            {
                _updater.SetPassive(cluster, destination, DestinationHealth.Healthy, ReactivationPeriod(cluster));
            }

            return;
        }

        window.Record(now, failed: true);
        var (total, failures) = window.Sum(now);
        if (total >= _minimalTotal && total > 0 && failures >= _failureRateLimit * total)
        {
            window.Clear();
            _updater.SetPassive(cluster, destination, DestinationHealth.Unhealthy, ReactivationPeriod(cluster));
        }
    }

    /// <summary>
    /// The active check saw <paramref name="destination"/> answer again after it had it down: what the passive side remembers is from the
    /// outage, so it is forgotten, and a passive verdict from the same outage is lifted.
    /// </summary>
    internal void ActiveRecovered(ClusterState cluster, DestinationState destination)
    {
        if (_windows.TryGetValue(destination, out var window))
        {
            window.Clear();
        }

        if (destination.Health.Passive == DestinationHealth.Unhealthy)
        {
            _updater.SetPassive(cluster, destination, DestinationHealth.Unknown, TimeSpan.Zero);
        }
    }

    /// <summary>Puts this policy in place of YARP's own; call after <c>AddReverseProxy()</c>.</summary>
    internal static void Replace(IServiceCollection services)
    {
        var replaced = 0;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IPassiveHealthCheckPolicy)
                && services[i].ImplementationType?.FullName == "Yarp.ReverseProxy.Health.TransportFailureRateHealthPolicy")
            {
                services.RemoveAt(i);
                replaced++;
            }
        }

        // A YARP that registers it differently would leave two policies of one name, or YARP's alone: either way not this one.
        if (replaced != 1)
        {
            throw new InvalidOperationException("YARP's TransportFailureRate passive policy wasn't found to replace.");
        }

        services.AddSingleton<PassiveFailureRatePolicy>();
        services.AddSingleton<IPassiveHealthCheckPolicy>(static provider => provider.GetRequiredService<PassiveFailureRatePolicy>());
    }

    private static TimeSpan ReactivationPeriod(ClusterState cluster) =>
        cluster.Model.Config.HealthCheck?.Passive?.ReactivationPeriod ?? DefaultReactivationPeriod;

    // YARP's own classification: a failure to reach the destination or read its response, not the client going away.
    private static bool Failed(HttpContext context)
    {
        if (context.Features.Get<IForwarderErrorFeature>() is not { } error || context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        return error.Error is ForwarderError.Request or ForwarderError.RequestTimedOut or ForwarderError.RequestBodyDestination
            or ForwarderError.ResponseBodyDestination or ForwarderError.UpgradeRequestDestination or ForwarderError.UpgradeResponseDestination;
    }
}

/// <summary>
/// One destination's requests and failures per second over the last few seconds. Each bucket is one <c>long</c>: the second (28 bits, so
/// a bucket is mistaken for a current one only after 8 years without traffic), the requests and the failures (18 bits each, saturating).
/// </summary>
internal sealed class FailureWindow
{
    public const int MaxSeconds = 3600;

    private const int CountBits = 18;
    private const long CountMax = (1L << CountBits) - 1;
    private const long StampMask = (1L << 28) - 1;

    private readonly long[] _buckets;

    public FailureWindow(int seconds) => _buckets = new long[seconds];

    public void Record(long second, bool failed)
    {
        ref var bucket = ref _buckets[second % _buckets.Length];
        var stamp = second & StampMask;
        while (true)
        {
            var old = Volatile.Read(ref bucket);
            long total = 1, failures = failed ? 1 : 0;
            if (Stamp(old) == stamp)
            {
                total = Math.Min(Total(old) + 1, CountMax);
                failures = Math.Min(Failures(old) + failures, CountMax);
            }

            if (Interlocked.CompareExchange(ref bucket, Pack(stamp, total, failures), old) == old)
            {
                return;
            }
        }
    }

    public (long Total, long Failures) Sum(long second)
    {
        var stamp = second & StampMask;
        long total = 0, failures = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            var value = Volatile.Read(ref _buckets[i]);
            if (((stamp - Stamp(value)) & StampMask) < _buckets.Length)
            {
                total += Total(value);
                failures += Failures(value);
            }
        }

        return (total, failures);
    }

    // A request recorded while this runs may survive it; one stray count can't take a destination out on its own.
    public void Clear() => Array.Clear(_buckets);

    private static long Pack(long stamp, long total, long failures) => (stamp << (2 * CountBits)) | (total << CountBits) | failures;

    private static long Stamp(long value) => (value >>> (2 * CountBits)) & StampMask;

    private static long Total(long value) => (value >>> CountBits) & CountMax;

    private static long Failures(long value) => value & CountMax;
}
