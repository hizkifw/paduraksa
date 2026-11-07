using System.Buffers;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Gate;

/// <summary>What the gate decided for one request.</summary>
internal enum GateAction : byte
{
    /// <summary>
    /// Abort the request (a policy Drop, or the default host set to Close). The zero value, so an uninitialised
    /// decision fails closed.
    /// </summary>
    Abort,

    /// <summary>The YARP endpoint is set; call the rest of the pipeline.</summary>
    Forward,

    /// <summary>Write <see cref="GateDecision.Response"/>.</summary>
    Respond,
}

/// <summary>The gate's synchronous verdict: 16 bytes, returned by value. <c>default</c> is <see cref="Abort"/>.</summary>
internal readonly record struct GateDecision(GateAction Action, PrecomputedResponse? Response)
{
    public static GateDecision Forward => new(GateAction.Forward, null);

    public static GateDecision Abort => default;

    public static GateDecision Respond(PrecomputedResponse response) => new(GateAction.Respond, response);
}

/// <summary>
/// The first middleware of the public pipeline (PLAN §7.2 C/D, spike S3). <see cref="Decide"/> is synchronous and
/// allocation-free. In order:
/// <list type="number">
/// <item>one snapshot read, per-request state from <see cref="IPersistentStateFeature"/>, the client address;</item>
/// <item>the ACME HTTP-01 hook (M3), which bypasses everything below;</item>
/// <item>host lookup; an unknown host gets the default behaviour; an HTTPS request for a host without HTTPS, or whose
/// connection is weaker than the host's TLS settings (HTTP/3, TLS version, ALPN; a connection coalesced from another
/// name), gets 421; a known host gets
/// its <c>Alt-Svc</c> on TCP HTTPS responses, whatever answers them;</item>
/// <item>the <b>host policies</b>, so they guard everything the host answers (a blocked client gets 403, not a redirect);</item>
/// <item>the HTTP→HTTPS redirect;</item>
/// <item>Redirect and Respond hosts;</item>
/// <item>route match (404 when none takes the path);</item>
/// <item>the <b>route's own policies</b>;</item>
/// <item><see cref="EndpointHttpContextExtensions.SetEndpoint"/> with YARP's endpoint from the same snapshot, so the
/// <c>UseRouting</c> that follows skips matching.</item>
/// </list>
/// Writing a response happens afterwards, in <see cref="CompleteAsync"/>.
/// </summary>
public sealed class GateMiddleware
{
    private static readonly object StateKey = new();

    private readonly SnapshotStore _store;
    private readonly IClientAddressSource _clientAddresses;

    public GateMiddleware(SnapshotStore store, IClientAddressSource clientAddresses)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clientAddresses);
        _store = store;
        _clientAddresses = clientAddresses;
    }

    /// <summary>For <c>app.Use(gate.InvokeAsync)</c>.</summary>
    public Task InvokeAsync(HttpContext context, RequestDelegate next) => CompleteAsync(context, Decide(context), next);

    /// <summary>
    /// The synchronous decision. Never writes the response; sets the endpoint when forwarding, and the host's
    /// <c>Alt-Svc</c> header (YARP doesn't copy an upstream's own <c>Alt-Svc</c>, so ours is the one sent).
    /// </summary>
    internal GateDecision Decide(HttpContext context)
    {
        RuntimeSnapshot snapshot = _store.Current;
        GateRequestState state = AcquireState(context);
        state.Snapshot = snapshot;
        state.ClientIp = _clientAddresses.Resolve(context, snapshot);
        state.IsHttps = state.ClientIp.Scheme.EffectiveIsHttps(context.Request.IsHttps);

        if (TryAnswerAcmeChallenge(context, snapshot, out GateDecision challenge))
        {
            return challenge;
        }

        HttpRequest request = context.Request;
        StringValues hostHeader = request.Headers.Host;
        CompiledHost? host = hostHeader.Count == 1 ? snapshot.Hosts.Find(hostHeader[0]) : null;
        if (host is null)
        {
            DefaultHostBehaviour fallback = snapshot.Default;
            return fallback.Action == DefaultHostAction.Close ? GateDecision.Abort : GateDecision.Respond(fallback.Response!);
        }

        state.Host = host;
        if (request.IsHttps && (!host.Https || !ConnectionTlsCheck.Satisfies(context, host.Tls)))
        {
            return GateDecision.Respond(snapshot.Misdirected);
        }

        AltSvcHeader.Apply(context, host.AltSvc);
        RequestView view = new(context, state);
        if (Stops(host.Policies, in view, out GateDecision stopped))
        {
            return stopped;
        }

        // The trusted scheme, not the connection's: behind a TLS-terminating trusted proxy the client already uses HTTPS.
        if (host.HttpsRedirect is { } httpsRedirect && !state.IsHttps)
        {
            return GateDecision.Respond(httpsRedirect.For(request.Method));
        }

        if (host.FixedResponse is { } fixedResponse)
        {
            return GateDecision.Respond(fixedResponse);
        }

        CompiledRoute? route = MatchRoute(host, request.Path.Value);
        if (route is null)
        {
            return GateDecision.Respond(snapshot.NoRoute);
        }

        state.Route = route;
        if (Stops(route.Policies, in view, out stopped))
        {
            return stopped;
        }

        Endpoint[] endpoints = snapshot.YarpEndpoints;
        if ((uint)route.Slot >= (uint)endpoints.Length)
        {
            return GateDecision.Respond(snapshot.Unavailable);
        }

        context.SetEndpoint(endpoints[route.Slot]);
        return GateDecision.Forward;
    }

    /// <summary>Carries out <paramref name="decision"/>: forward, write the precomputed response, or abort.</summary>
    internal static Task CompleteAsync(HttpContext context, GateDecision decision, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        switch (decision.Action)
        {
            case GateAction.Forward:
                return next(context);
            case GateAction.Respond:
                return ResponseWriter.WriteAsync(context, decision.Response!);
            default:
                context.Abort();
                return Task.CompletedTask;
        }
    }

    /// <summary>Runs a chain; true (with the decision) when a policy responds or drops.</summary>
    private static bool Stops(IRequestPolicy[] policies, in RequestView view, out GateDecision decision)
    {
        foreach (IRequestPolicy policy in policies)
        {
            PolicyDecision verdict = policy.Evaluate(in view);
            switch (verdict.Action)
            {
                case PolicyAction.Continue:
                    continue;
                case PolicyAction.Respond:
                    decision = GateDecision.Respond(verdict.Response!);
                    return true;
                default:
                    decision = GateDecision.Abort;
                    return true;
            }
        }

        decision = default;
        return false;
    }

    /// <summary>
    /// SEAM (M3): answers <c>/.well-known/acme-challenge/{token}</c> on plain HTTP from <c>IAcmeChallengeStore</c>,
    /// before host lookup and every policy, so a challenge can't be blocked, redirected to HTTPS or answered by the
    /// default host (PLAN §7.2 C). Nothing is answered until the challenge store exists.
    /// </summary>
    private static bool TryAnswerAcmeChallenge(HttpContext context, RuntimeSnapshot snapshot, out GateDecision decision)
    {
        // TODO(M3): if (!context.Request.IsHttps && path starts with "/.well-known/acme-challenge/") look the token
        // up in the challenge store and answer 200 with the key authorisation (precomputed bytes), else fall through.
        _ = context;
        _ = snapshot;
        decision = default;
        return false;
    }

    /// <summary>
    /// Matches on the canonical view of the path (<see cref="RoutePath"/>) without allocating: as is when it's already
    /// canonical, else rewritten on the stack, or in a pooled buffer past <see cref="RoutePath.StackLimit"/> chars.
    /// </summary>
    private static CompiledRoute? MatchRoute(CompiledHost host, ReadOnlySpan<char> path)
    {
        if (RoutePath.IsCanonical(path))
        {
            return host.Match(path);
        }

        if (path.Length < RoutePath.StackLimit)
        {
            Span<char> buffer = stackalloc char[RoutePath.StackLimit];
            return host.Match(buffer[..RoutePath.Canonicalize(path, buffer)]);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
        try
        {
            return host.Match(rented.AsSpan(0, RoutePath.Canonicalize(path, rented)));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The pooled per-request state (spike S3 §3). Kestrel keeps <see cref="IPersistentStateFeature"/> on the H1
    /// connection or the pooled H2/H3 stream, so this allocates only for a new connection or stream; without the
    /// feature (non-Kestrel servers) it allocates per request.
    /// </summary>
    private static GateRequestState AcquireState(HttpContext context)
    {
        GateRequestState state;
        IDictionary<object, object?>? persistent = context.Features.Get<IPersistentStateFeature>()?.State;
        if (persistent is not null && persistent.TryGetValue(StateKey, out object? cached) && cached is GateRequestState reused)
        {
            state = reused;
            state.Reset();
        }
        else
        {
            state = new GateRequestState();
            if (persistent is not null)
            {
                persistent[StateKey] = state;
            }
        }

        context.Features.Set<IGateFeature>(state);
        return state;
    }
}
