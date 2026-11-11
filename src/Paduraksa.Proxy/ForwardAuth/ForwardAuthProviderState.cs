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
    // The value _inFlight takes once the client is disposed. The counter is both the number of calls running and the lifecycle's gate: a call takes a place by
    // compare-and-swap and refuses a negative value, the disposal moves 0 (nobody running) to Closed in one step, so no call can start on a client that is
    // being disposed and none can create it afterwards.
    private const int Closed = int.MinValue;

    private static readonly TimeSpan DisposalMargin = TimeSpan.FromSeconds(10);

    private readonly ForwardAuthRuntime _runtime;
    private readonly Func<AuthProviderSpec, HttpMessageHandler>? _handlerFactory;
    private readonly AuthProviderSpec _spec;
    private readonly object _lifecycle = new();
    private volatile Lazy<HttpClient> _client;
    private int _inFlight;
    private volatile bool _retired;
    private int _generation;
    private ITimer? _disposal;

    // Written by ForwardAuthRuntime.ReportFailure: when the last failure was logged, so a dead endpoint costs one line a minute.
    internal long LastReportedAt;

    // The same for an identity header left off (ForwardAuthPolicy), which is another message with a minute of its own.
    internal long LastDroppedReportedAt;

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
        _client = NewClient();
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

    /// <summary>The client every call goes through; made on first use. Only a call that holds a place (<see cref="TryEnter"/>) may use it.</summary>
    internal HttpClient Client => !IsDisposed ? _client.Value : throw new ObjectDisposedException(nameof(ForwardAuthProviderState));

    /// <summary>Whether the client has been disposed (and no <see cref="Revive"/> has made a new one), for tests and for the policy's answer.</summary>
    internal bool IsDisposed => Volatile.Read(ref _inFlight) < 0;

    /// <summary>Whether the state is one a snapshot may be compiled on: not retired and not disposed.</summary>
    internal bool IsLive => !_retired && !IsDisposed;

    /// <summary>
    /// The configuration that replaced or dropped this state went live. Snapshots published earlier still hold it, and a request that chose its route
    /// on one of them may still make its call (within a few milliseconds of choosing) or be waiting on one (for at most the timeout), so the client is
    /// not disposed at once: it is after the timeout and a margin, and not while a call still runs. A call that begins later than that fails closed
    /// (<see cref="TryEnter"/> refuses a disposed state, answered 502). The runtime holds a retired state until it is disposed or comes back
    /// (<see cref="Revive"/>), so the timer that disposes it can't be collected with it.
    /// </summary>
    /// <returns>False when it was retired or disposed already.</returns>
    internal bool Retire()
    {
        lock (_lifecycle)
        {
            if (_retired || IsDisposed)
            {
                return false;
            }

            _retired = true;
            _disposal?.Dispose();
            _disposal = _runtime.Time.CreateTimer(
                static tick => ((Tick)tick!).State.DisposeWhenIdle(((Tick)tick).Generation),
                new Tick(this, ++_generation),
                _spec.Timeout + DisposalMargin,
                Timeout.InfiniteTimeSpan);
            _runtime.Hold(this);
            return true;
        }
    }

    /// <summary>
    /// A snapshot that holds this state is about to go live again: a commit that reverted a change compiled while the one that made it was publishing, so
    /// it bound the state the other retired. It is live again, its disposal cancelled. When it was disposed already (the publish waited longer than the
    /// grace period), it gets a new client and keeps its cache: every snapshot that holds it, this one included, runs on this object, so a new object
    /// beside it would leave them calling a disposed client.
    /// </summary>
    internal void Revive()
    {
        lock (_lifecycle)
        {
            if (IsDisposed)
            {
                _client = NewClient();
                Volatile.Write(ref _inFlight, 0);
            }
            else if (!_retired)
            {
                return;
            }

            _retired = false;

            // A tick that is already on its way finds another generation and does nothing.
            _generation++;
            _disposal?.Dispose();
            _disposal = null;
            _runtime.Release(this);
        }
    }

    private void DisposeWhenIdle(int generation)
    {
        lock (_lifecycle)
        {
            if (!_retired || generation != _generation)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _inFlight, Closed, 0) != 0)
            {
                _disposal?.Change(DisposalMargin, Timeout.InfiniteTimeSpan);
                return;
            }

            if (_client.IsValueCreated)
            {
                _client.Value.Dispose();
            }

            _disposal?.Dispose();
            _disposal = null;
            _runtime.Release(this);
        }
    }

    /// <summary>Takes one of the places for a running call; false when they are all taken or the client is disposed (<see cref="IsDisposed"/> tells which).</summary>
    internal bool TryEnter()
    {
        var running = Volatile.Read(ref _inFlight);
        while (true)
        {
            if (running < 0 || running >= ForwardAuthLimits.MaxConcurrentCalls)
            {
                return false;
            }

            var seen = Interlocked.CompareExchange(ref _inFlight, running + 1, running);
            if (seen == running)
            {
                return true;
            }

            running = seen;
        }
    }

    internal void Exit() => Interlocked.Decrement(ref _inFlight);

    private Lazy<HttpClient> NewClient() => new(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

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

            // A provider writes a user's name as UTF-8 (authentik's X-authentik-name), as Kestrel reads a client's headers. The default is Latin-1, which would show
            // each byte of "é" as a character of its own and have it encoded again on the way upstream.
            ResponseHeaderEncodingSelector = static (_, _) => System.Text.Encoding.UTF8,
        };

        // The timeout is the call's own (a token that also ends with the client's request), not the client's.
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // A timer's state: the generation it was made for, so one that fires after a revive (or a second retirement) is told apart from the current one.
    private sealed record Tick(ForwardAuthProviderState State, int Generation);
}
