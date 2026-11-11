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
/// and connections); one that changed gets a new state, so a changed endpoint, header list or scope never reuses an old decision.
/// </summary>
public sealed partial class ForwardAuthRuntime
{
    private readonly ConcurrentDictionary<AuthProviderId, ForwardAuthProviderState> _providers = new();
    private readonly ConcurrentDictionary<AuthProviderId, ForwardAuthCounters> _counters = new();
    private readonly Func<AuthProviderSpec, HttpMessageHandler>? _handlerFactory;
    private readonly ILogger? _logger;

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

    /// <summary>The live state of a provider, or null when the process has none (never published, or retired).</summary>
    public ForwardAuthProviderState? StateOf(AuthProviderId id) => _providers.GetValueOrDefault(id);

    /// <summary>
    /// The state <paramref name="spec"/> runs on: the one the process already has when its configuration hash is the same, else a new one.
    /// <paramref name="publish"/> records it as the provider's live state; a compile that will never go live (a dry run, a draft, a
    /// simulation) passes false and leaves the process exactly as it found it. Either way the HTTP client is made on the first call, so
    /// compiling opens nothing.
    /// </summary>
    internal ForwardAuthProviderState Bind(AuthProviderSpec spec, bool publish)
    {
        var hash = ForwardAuthProviderState.HashOf(spec);
        if (_providers.TryGetValue(spec.Id, out var existing) && string.Equals(existing.ConfigHash, hash, StringComparison.Ordinal))
        {
            return existing;
        }

        var created = new ForwardAuthProviderState(this, spec, hash, CountersFor(spec.Id), _handlerFactory);
        if (publish)
        {
            _providers[spec.Id] = created;
        }

        return created;
    }

    /// <summary>Forgets the providers a published config no longer names. The old states stay reachable from the snapshots that still hold them; their idle connections close on their own.</summary>
    internal void Retain(IReadOnlySet<AuthProviderId> live)
    {
        foreach (var id in _providers.Keys)
        {
            if (!live.Contains(id))
            {
                _providers.TryRemove(id, out _);
            }
        }
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
            string.Join(',', spec.RequestHeaders.Select(static h => h.ToUpperInvariant()).Order(StringComparer.Ordinal)),
            string.Join(',', spec.ResponseHeaders.Select(static h => h.ToUpperInvariant()).Order(StringComparer.Ordinal)),
            spec.SignInUrl ?? string.Empty,
            spec.SignInReturnParameter ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }
}
