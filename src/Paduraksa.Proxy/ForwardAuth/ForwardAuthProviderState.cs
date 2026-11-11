using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>
/// One auth provider at one configuration: the cache of its allows, the client that calls its endpoint, how many calls are running and
/// the counters (which outlive the configuration). It is shared by every snapshot whose provider has the same <see cref="ConfigHash"/>, so a
/// publish that changes something else keeps the cache and the pooled connections, and one that changes the provider starts a new state.
/// </summary>
public sealed class ForwardAuthProviderState
{
    private readonly ForwardAuthRuntime _runtime;
    private readonly Func<AuthProviderSpec, HttpMessageHandler>? _handlerFactory;
    private readonly AuthProviderSpec _spec;
    private readonly Lazy<HttpClient> _client;
    private int _inFlight;
    private int _retired;
    private int _disposed;
    private ITimer? _disposal;

    // Written by ForwardAuthRuntime.ReportFailure: when the last failure was logged, so a dead endpoint costs one line a minute.
    internal long LastReportedAt;

    internal ForwardAuthProviderState(
        ForwardAuthRuntime runtime, AuthProviderSpec spec, string configHash, ForwardAuthCounters counters, Func<AuthProviderSpec, HttpMessageHandler>? handlerFactory)
    {
        _runtime = runtime;
        _spec = spec;
        _handlerFactory = handlerFactory;
        ConfigHash = configHash;
        Counters = counters;
        Uri = new Uri(spec.Url, UriKind.Absolute);
        Cache = new ForwardAuthDecisionCache();
        TtlTicks = (long)(spec.CacheTtl.TotalSeconds * runtime.Time.TimestampFrequency);
        _client = new Lazy<HttpClient>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Hashes a spec the way <see cref="ForwardAuthRuntime.Bind"/> compares them.</summary>
    internal static string HashOf(AuthProviderSpec spec) => ForwardAuthRuntime.Hash(spec);

    /// <summary>What the state was built for: equal hashes mean the same cache and client serve both.</summary>
    public string ConfigHash { get; }

    public ForwardAuthCounters Counters { get; }

    public AuthProviderId Id => _spec.Id;

    public string Url => _spec.Url;

    internal Uri Uri { get; }

    internal ForwardAuthDecisionCache Cache { get; }

    internal ForwardAuthRuntime Runtime => _runtime;

    /// <summary>How long an allow lives, in <see cref="TimeProvider.GetTimestamp"/> ticks; 0 = not remembered.</summary>
    internal long TtlTicks { get; }

    /// <summary>The allows remembered and still valid, for tests and diagnostics.</summary>
    public int CachedAllows => Cache.Count(_runtime.Time.GetTimestamp());

    /// <summary>The client every call goes through; made on first use.</summary>
    internal HttpClient Client => Volatile.Read(ref _disposed) == 0 ? _client.Value : throw new ObjectDisposedException(nameof(ForwardAuthProviderState));

    /// <summary>
    /// The configuration that replaced or dropped this state went live. Snapshots published earlier still hold it, and a request that chose its route
    /// on one of them may still make its call (within a few milliseconds of choosing) or be waiting on one (for at most the timeout), so the client is
    /// not disposed at once: it is after the timeout and a margin, and not while a call still runs. A call that begins later than that fails closed
    /// (<see cref="ObjectDisposedException"/>, answered 502).
    /// </summary>
    internal void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
        {
            return;
        }

        _disposal = _runtime.Time.CreateTimer(static s => ((ForwardAuthProviderState)s!).DisposeWhenIdle(), this, _spec.Timeout + DisposalMargin, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether the client has been disposed, for tests.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static readonly TimeSpan DisposalMargin = TimeSpan.FromSeconds(10);

    private void DisposeWhenIdle()
    {
        if (Volatile.Read(ref _inFlight) > 0)
        {
            _disposal?.Change(DisposalMargin, Timeout.InfiniteTimeSpan);
            return;
        }

        Interlocked.Exchange(ref _disposed, 1);
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }

        _disposal?.Dispose();
    }

    /// <summary>Takes one of the places for a running call; false when they are all taken.</summary>
    internal bool TryEnter()
    {
        if (Interlocked.Increment(ref _inFlight) <= Paduraksa.Core.Model.ForwardAuthLimits.MaxConcurrentCalls)
        {
            return true;
        }

        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    internal void Exit() => Interlocked.Decrement(ref _inFlight);

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler = _handlerFactory?.Invoke(_spec) ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // The endpoint is the operator's own service, named by address: an environment proxy would send the sub-request somewhere else.
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = _spec.Timeout + TimeSpan.FromSeconds(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Paduraksa.Core.Model.ForwardAuthLimits.MaxConcurrentCalls,
        };

        // The timeout is the call's own (a token that also ends with the client's request), not the client's.
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
