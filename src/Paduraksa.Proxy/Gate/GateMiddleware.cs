using System.Buffers;
using System.Diagnostics;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
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
/// <item>the ACME HTTP-01 answer, for a token the orchestrator published, which bypasses everything below;</item>
/// <item>host lookup; an unknown host gets the default behaviour; the host's response-header list, first stage
/// (its <c>Server</c>), so every answer below carries it; an HTTPS request for a host without HTTPS, or whose
/// connection is weaker than the host's TLS settings (HTTP/3, TLS version, ALPN; a connection coalesced from another
/// name), gets 421; the list's second stage (its <c>Alt-Svc</c>, <c>Strict-Transport-Security</c>) on the answers after
/// that check;</item>
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
    private readonly AcmeChallengeStore? _challenges;

    /// <param name="store">The live snapshot.</param>
    /// <param name="clientAddresses">Resolves the client address.</param>
    /// <param name="challenges">The HTTP-01 answers; without it the gate answers no challenge.</param>
    public GateMiddleware(SnapshotStore store, IClientAddressSource clientAddresses, AcmeChallengeStore? challenges = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clientAddresses);
        _store = store;
        _clientAddresses = clientAddresses;
        _challenges = challenges;
    }

    /// <summary>For <c>app.Use(gate.InvokeAsync)</c>.</summary>
    public Task InvokeAsync(HttpContext context, RequestDelegate next) => InvokeCoreAsync(context, next);

    /// <summary>
    /// <see cref="Decide"/>, then the response headers of the last stage (<see cref="ResponseHeaderStage.AfterDecision"/>:
    /// the response <c>X-Request-Id</c>), then <see cref="CompleteAsync"/>. The ID's header value is the one string a
    /// request accepts; it is materialised here, once, and shared with the upstream header and the access log, so
    /// <see cref="Decide"/> itself stays at 0 B.
    /// </summary>
    private Task InvokeCoreAsync(HttpContext context, RequestDelegate next)
    {
        GateDecision decision = Decide(context);
        GateRequestState? state = context.Features.Get<IGateFeature>()?.State;
        if (state is not null)
        {
            ResponseHeaderOperation[] headers = state.Host is { } host ? host.ResponseHeaders : state.Snapshot?.DefaultResponseHeaders ?? [];
            ResponseHeaders.ApplyStage(context, headers, ResponseHeaderStage.AfterDecision, state, [], applyRequestId: true);
        }

        return CompleteAsync(context, decision, next);
    }

    /// <summary>
    /// The synchronous decision. Never writes the response; sets the endpoint when forwarding, and applies the host's
    /// compiled response-header list by stage (<see cref="ResponseHeaders"/>, <see cref="CompiledHost.ResponseHeaders"/>).
    /// The response <c>X-Request-Id</c> waits for the answer, because its value is the one string a request materialises.
    /// </summary>
    internal GateDecision Decide(HttpContext context) => Decide(context, _store.Current, _clientAddresses, _challenges, default(NoGateTracer));

    /// <summary>
    /// The decision over <paramref name="snapshot"/>, reporting every step to <paramref name="tracer"/>. The one body
    /// production (<see cref="NoGateTracer"/>, which the JIT removes) and the request simulator (PLAN §7.7) both run.
    /// </summary>
    internal static GateDecision Decide<TTracer>(HttpContext context, RuntimeSnapshot snapshot, IClientAddressSource clientAddresses, AcmeChallengeStore? challenges, TTracer tracer)
        where TTracer : struct, IGateTracer
    {
        GateRequestState state = AcquireState(context);
        state.Snapshot = snapshot;
        ClientIpResolution client = clientAddresses.Resolve(context, snapshot);
        state.ClientIp = client;
        state.IsHttps = client.Scheme.EffectiveIsHttps(context.Request.IsHttps);
        // The same trust decision the forwarding headers make: a trusted proxy may name the request; anyone else's header
        // is replaced (PLAN §7.4). 0 B: the header value is adopted by reference, or only a counter is taken.
        state.InitRequestId(context.Request, client.PeerTrusted);
        tracer.ClientResolved(context, snapshot, in client, state.IsHttps);

        bool answered = TryAnswerAcmeChallenge(challenges, context, out GateDecision challenge);
        tracer.AcmeChallenge(context, answered);
        if (answered)
        {
            ApplyHeaders(context, snapshot.DefaultResponseHeaders, ResponseHeaderStage.AfterHostLookup, state, null, tracer);
            return challenge;
        }

        HttpRequest request = context.Request;
        StringValues hostHeader = request.Headers.Host;
        HostMatchKind matched = HostMatchKind.None;
        CompiledHost? host = hostHeader.Count == 1 ? snapshot.Hosts.Find(hostHeader[0], out matched) : null;
        tracer.HostLookup(hostHeader, host, matched, snapshot.Default);
        if (host is null)
        {
            DefaultHostBehaviour fallback = snapshot.Default;
            if (fallback.Action == DefaultHostAction.Close)
            {
                return GateDecision.Abort;
            }

            ApplyHeaders(context, snapshot.DefaultResponseHeaders, ResponseHeaderStage.AfterHostLookup, state, null, tracer);
            return GateDecision.Respond(fallback.Response!);
        }

        state.Host = host;
        ApplyHeaders(context, host.ResponseHeaders, ResponseHeaderStage.AfterHostLookup, state, host, tracer);
        bool connectionIsHttps = request.IsHttps;
        TlsMismatch mismatch = connectionIsHttps
            ? host.Https ? ConnectionTlsCheck.Check(context, host.Tls) : TlsMismatch.HostNotServedOverHttps
            : TlsMismatch.None;
        tracer.TlsCheck(context, host, connectionIsHttps, mismatch);
        if (mismatch != TlsMismatch.None)
        {
            return GateDecision.Respond(snapshot.Misdirected);
        }

        ApplyHeaders(context, host.ResponseHeaders, ResponseHeaderStage.AfterTlsCheck, state, host, tracer);
        RequestView view = new(context, state, TTracer.IsSimulation);
        if (Stops(PolicyScope.Host, host.Policies, in view, tracer, out GateDecision stopped))
        {
            return stopped;
        }

        // The trusted scheme, not the connection's: behind a TLS-terminating trusted proxy the client already uses HTTPS.
        if (host.HttpsRedirect is { } httpsRedirect)
        {
            bool redirect = !state.IsHttps;
            tracer.HttpsRedirect(host, state.IsHttps, redirect);
            if (redirect)
            {
                return GateDecision.Respond(httpsRedirect.For(request.Method));
            }
        }

        if (host.FixedResponse is { } fixedResponse)
        {
            tracer.FixedResponse(host);
            return GateDecision.Respond(fixedResponse);
        }

        CompiledRoute? route = MatchRoute(host, request.Path.Value, tracer);
        if (route is null)
        {
            return GateDecision.Respond(snapshot.NoRoute);
        }

        state.Route = route;
        if (Stops(PolicyScope.Route, route.Policies, in view, tracer, out stopped))
        {
            return stopped;
        }

        Endpoint[] endpoints = snapshot.YarpEndpoints;
        bool endpointPresent = (uint)route.Slot < (uint)endpoints.Length;
        tracer.Forward(snapshot, route, endpointPresent);
        if (!endpointPresent)
        {
            return GateDecision.Respond(snapshot.Unavailable);
        }

        context.SetEndpoint(endpoints[route.Slot]);
        return GateDecision.Forward;
    }

    /// <summary>
    /// Applies one stage of a host's response-header list (PLAN §7.2 C) and reports it: the request ID waits for the
    /// answer, so the decision itself allocates nothing.
    /// </summary>
    private static void ApplyHeaders<TTracer>(
        HttpContext context,
        ResponseHeaderOperation[] headers,
        ResponseHeaderStage stage,
        GateRequestState state,
        CompiledHost? host,
        TTracer tracer)
        where TTracer : struct, IGateTracer
    {
        // The compiler caps a host's list at this size, so the buffer needs no length from the snapshot to be safe. If a
        // hand-built list ever gets past it, the trace loses the tail rather than the request failing.
        Debug.Assert(
            headers.Length <= ResponseHeaders.MaxResponseHeaderOperations,
            "The compiler must cap a host's response headers at the gate's buffer size.");
        Span<HeaderOutcome> outcomes = stackalloc HeaderOutcome[ResponseHeaders.MaxResponseHeaderOperations];
        outcomes.Clear();
        Span<HeaderOutcome> used = outcomes[..Math.Min(headers.Length, ResponseHeaders.MaxResponseHeaderOperations)];
        ResponseHeaders.ApplyStage(context, headers, stage, state, used, applyRequestId: false);
        if (stage == ResponseHeaderStage.AfterHostLookup)
        {
            // Every answer this step reaches carries the last stage's header (the request ID, whose string the answer
            // materialises), so it is described here: the first pass runs on every traced exit, the 421 and an unknown
            // host included.
            ResponseHeaders.DescribeDeferred(headers, ResponseHeaderStage.AfterDecision, used);
        }

        tracer.ResponseHeaders(context, host, headers, used);
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
                // Nothing is sent: the access log says so rather than inventing the 200 a response would have had.
                if (context.Features.Get<IGateFeature>()?.State is { } aborted)
                {
                    aborted.Aborted = true;
                }

                context.Abort();
                return Task.CompletedTask;
        }
    }

    /// <summary>Runs a chain; true (with the decision) when a policy responds or drops.</summary>
    private static bool Stops<TTracer>(PolicyScope scope, IRequestPolicy[] policies, in RequestView view, TTracer tracer, out GateDecision decision)
        where TTracer : struct, IGateTracer
    {
        for (int i = 0; i < policies.Length; i++)
        {
            IRequestPolicy policy = policies[i];
            PolicyDecision verdict = policy.Evaluate(in view);
            tracer.PolicyEvaluated(scope, i, policy, in view, verdict);
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
    /// Answers <c>GET</c> and <c>HEAD /.well-known/acme-challenge/{token}</c> on plain HTTP with the key authorization of a
    /// token the orchestrator published (<c>text/plain</c>, 200), before host lookup and every policy, so a challenge
    /// can't be blocked, redirected to HTTPS or answered by the default host (PLAN §7.2 C). The token is the rest of the
    /// path, compared exactly; anything else (another method, HTTPS, an unknown or nested token) falls through to the
    /// normal pipeline, where it is a request like any other: that is how an upstream that runs its own ACME client
    /// keeps serving its own challenges. 0 B.
    /// </summary>
    private static bool TryAnswerAcmeChallenge(AcmeChallengeStore? challenges, HttpContext context, out GateDecision decision)
    {
        decision = default;
        HttpRequest request = context.Request;
        if (challenges is null || request.IsHttps || request.Path.Value is not { } path
            || !path.StartsWith(AcmeChallengeStore.PathPrefix, StringComparison.Ordinal)
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            return false;
        }

        if (!challenges.TryGet(path.AsSpan(AcmeChallengeStore.PathPrefix.Length), out PrecomputedResponse? response))
        {
            return false;
        }

        decision = GateDecision.Respond(response);
        return true;
    }

    /// <summary>
    /// Matches on the canonical view of the path (<see cref="RoutePath"/>) without allocating: as is when it's already
    /// canonical, else rewritten on the stack, or in a pooled buffer past <see cref="RoutePath.StackLimit"/> chars.
    /// </summary>
    private static CompiledRoute? MatchRoute<TTracer>(CompiledHost host, ReadOnlySpan<char> path, TTracer tracer)
        where TTracer : struct, IGateTracer
    {
        CompiledRoute? route;
        if (RoutePath.IsCanonical(path))
        {
            route = host.Match(path);
            tracer.RouteMatch(host, path, path, route);
            return route;
        }

        if (path.Length < RoutePath.StackLimit)
        {
            Span<char> buffer = stackalloc char[RoutePath.StackLimit];
            ReadOnlySpan<char> canonical = buffer[..RoutePath.Canonicalize(path, buffer)];
            route = host.Match(canonical);
            tracer.RouteMatch(host, path, canonical, route);
            return route;
        }

        char[] rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
        try
        {
            ReadOnlySpan<char> canonical = rented.AsSpan(0, RoutePath.Canonicalize(path, rented));
            route = host.Match(canonical);
            tracer.RouteMatch(host, path, canonical, route);
            return route;
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
