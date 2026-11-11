using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>
/// What the forward-auth policies need from outside the immutable snapshot: the decision caches, the HTTP clients that call the
/// endpoints, the counters and the secret the cache keys are hashed with. One per process, handed to the compiler like the access-list
/// runtime. A snapshot is rebuilt on every publish, but a provider whose effective configuration didn't change keeps its state (cache, client
/// and connections); one that changed gets a new state, so a changed endpoint, header list or scope never reuses an old decision. Only
/// <see cref="Publish"/>, which the publisher calls as a snapshot goes live, changes which states are live.
/// </summary>
public sealed partial class ForwardAuthRuntime
{
    private readonly ConcurrentDictionary<AuthProviderId, ForwardAuthProviderState> _providers = new();
    private readonly ConcurrentDictionary<AuthProviderId, ForwardAuthCounters> _counters = new();

    // The retired states that haven't been disposed yet. A state is reachable only from the snapshots that hold it, and its disposal timer only from the state:
    // holding it here keeps the timer from being collected before it fires.
    private readonly ConcurrentDictionary<ForwardAuthProviderState, byte> _retiring = new();
    private readonly Func<AuthProviderSpec, HttpMessageHandler>? _handlerFactory;
    private readonly ILogger? _logger;
    private readonly object _publishLock = new();

    public ForwardAuthRuntime()
        : this(TimeProvider.System, null)
    {
    }

    /// <param name="time">The clock the decision caches expire by.</param>
    /// <param name="logger">Where a failing endpoint is reported, at most once a minute per provider; null: nowhere.</param>
    /// <param name="handlerFactory">The handler a provider's client sends with; null: a <see cref="SocketsHttpHandler"/> with no redirects, cookies or proxy. A test's seam.</param>
    public ForwardAuthRuntime(TimeProvider time, ILogger? logger, Func<AuthProviderSpec, HttpMessageHandler>? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        Time = time;
        _logger = logger;
        _handlerFactory = handlerFactory;
        CacheSecret = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>The runtime a compile uses when its options name none.</summary>
    public static ForwardAuthRuntime Default { get; } = new();

    public TimeProvider Time { get; }

    /// <summary>The per-process secret the cache keys are hashed with. Not persisted, and not a vault key: a restart empties the caches anyway,
    /// and no key is ever shown to anyone.</summary>
    internal byte[] CacheSecret { get; }

    /// <summary>The counters of <paramref name="id"/>: cold path, once per provider per compile.</summary>
    public ForwardAuthCounters CountersFor(AuthProviderId id) => _counters.GetOrAdd(id, static _ => new ForwardAuthCounters());

    /// <summary>How many retired states wait to be disposed, for tests.</summary>
    internal int RetiredStates => _retiring.Count;

    internal void Hold(ForwardAuthProviderState state) => _retiring[state] = 0;

    internal void Release(ForwardAuthProviderState state) => _retiring.TryRemove(state, out _);

    /// <summary>The live state of a provider, or null when the process has none (never published, or retired).</summary>
    public ForwardAuthProviderState? StateOf(AuthProviderId id) => _providers.GetValueOrDefault(id);

    /// <summary>
    /// The state <paramref name="spec"/> runs on: the one the process already has when its configuration hash is the same and it is live (never one that is
    /// retired or disposed), else a new one that nothing records. Compiling changes no state, whether or not the result goes live (a failed publish, a
    /// dry run, a draft and a simulation all leave the live ones as they found them); the only trace is the counters entry of a provider ID the process hasn't
    /// seen (<see cref="CountersFor"/>), which no snapshot can lose. <see cref="Publish"/> makes a snapshot's states the live ones. The HTTP client is made on the
    /// first call, so compiling opens nothing either.
    /// </summary>
    internal ForwardAuthProviderState Bind(AuthProviderSpec spec)
    {
        var hash = ForwardAuthProviderState.HashOf(spec);
        return _providers.TryGetValue(spec.Id, out var existing) && existing.IsLive && string.Equals(existing.ConfigHash, hash, StringComparison.Ordinal)
            ? existing
            : new ForwardAuthProviderState(this, spec, hash, CountersFor(spec.Id), _handlerFactory);
    }

    /// <summary>
    /// Makes <paramref name="states"/>, the ones a snapshot that is about to go live runs on, the process's live providers: the ones it no longer
    /// has or has replaced are retired. A retired state stays reachable from the snapshots that still hold it, so its client is disposed only
    /// after a grace period and once no call is running (<see cref="ForwardAuthProviderState.Retire"/>). A state in <paramref name="states"/> that an earlier
    /// publish retired meanwhile (this snapshot was compiled while the one that replaced it was publishing, and reverted the change) is made live again
    /// (<see cref="ForwardAuthProviderState.Revive"/>) before anything else, so no live provider is ever one that is about to be disposed. The publisher calls it with its lock held.
    /// </summary>
    internal void Publish(IReadOnlyList<ForwardAuthProviderState> states)
    {
        lock (_publishLock)
        {
            foreach (var state in states)
            {
                state.Revive();
            }

            List<ForwardAuthProviderState> retired = [];
            foreach (var old in _providers.Values)
            {
                if (!Contains(states, old))
                {
                    retired.Add(old);
                }
            }

            foreach (var state in states)
            {
                _providers[state.Id] = state;
            }

            foreach (var old in retired)
            {
                _providers.TryRemove(new KeyValuePair<AuthProviderId, ForwardAuthProviderState>(old.Id, old));
                old.Retire();
            }
        }
    }

    private static bool Contains(IReadOnlyList<ForwardAuthProviderState> states, ForwardAuthProviderState state)
    {
        foreach (var candidate in states)
        {
            if (ReferenceEquals(candidate, state))
            {
                return true;
            }
        }

        return false;
    }

    internal void ReportFailure(ForwardAuthProviderState state, string name, string what)
    {
        if (_logger is null)
        {
            return;
        }

        var now = Time.GetTimestamp();
        var last = Interlocked.Read(ref state.LastReportedAt);
        if (last != 0 && Time.GetElapsedTime(last, now) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref state.LastReportedAt, now, last) == last)
        {
            LogFailure(_logger, name, state.Url, what);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The auth provider '{Name}' ({Url}) failed: {What}. Requests to the hosts and routes it guards are refused until it answers.")]
    private static partial void LogFailure(ILogger logger, string name, string url, string what);

    /// <summary>The configuration hash of a provider: everything that changes what its endpoint is asked or how its answer is read, and nothing
    /// that doesn't (its name and notes).</summary>
    internal static string Hash(AuthProviderSpec spec)
    {
        var text = string.Join(
            '\n',
            spec.Url,
            spec.Timeout.Ticks,
            spec.CacheTtl.Ticks,
            (int)spec.CacheScope,
            SortedUpper(spec.RequestHeaders),
            SortedUpper(spec.ResponseHeaders),
            spec.SignInUrl ?? string.Empty,
            spec.SignInReturnParameter ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }

    // A header list is a set: the order it was written in changes nothing, so it doesn't change the hash.
    private static string SortedUpper(IReadOnlyList<string> names)
    {
        var upper = new string[names.Count];
        for (var i = 0; i < upper.Length; i++)
        {
            upper[i] = names[i].ToUpperInvariant();
        }

        Array.Sort(upper, StringComparer.Ordinal);
        return string.Join(',', upper);
    }
}
