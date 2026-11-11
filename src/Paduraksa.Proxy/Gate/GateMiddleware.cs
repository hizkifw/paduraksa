using System.Buffers;
using System.Diagnostics;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Metrics;
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

    /// <summary>The gate's own proof-of-work verify endpoint: <see cref="GateMiddleware.InvokeAsync"/> reads the body, checks it
    /// and answers with the cookie and a redirect.</summary>
    VerifyProofOfWork,

    /// <summary>A policy deferred its verdict (<see cref="GateRequestState.DeferredPolicy"/>): <see cref="GateMiddleware.InvokeAsync"/>
    /// awaits it, then resumes the chain after that policy. Only the production decision returns it, never a simulation.</summary>
    AwaitPolicy,
}

/// <summary>The gate's synchronous verdict, 24 bytes: the action, the answer and its precomputed <c>Retry-After</c>.
/// Returned by value; <c>default</c> is <see cref="Abort"/>.</summary>
internal readonly record struct GateDecision(GateAction Action, PrecomputedResponse? Response, string? RetryAfter = null)
{
    public static GateDecision Forward => new(GateAction.Forward, null);

    public static GateDecision Abort => default;

    public static GateDecision Respond(PrecomputedResponse response) => new(GateAction.Respond, response);

    public static GateDecision Respond(PrecomputedResponse response, string? retryAfter) => new(GateAction.Respond, response, retryAfter);

    public static GateDecision VerifyProofOfWork => new(GateAction.VerifyProofOfWork, null);

    public static GateDecision AwaitPolicy => new(GateAction.AwaitPolicy, null);
}

/// <summary>
/// The first middleware of the public pipeline. <see cref="Decide"/> is synchronous and allocation-free; writing a
/// response happens afterwards, in <see cref="CompleteAsync"/>. The order is the contract: a host's policies run before
/// its redirect and routes, so a blocked client gets 403 rather than a redirect, and the route's own policies run after
/// the route match. The endpoint is set with YARP's own, so the <c>UseRouting</c> that follows skips matching.
/// </summary>
public sealed class GateMiddleware
{
    private static readonly object StateKey = new();

    private readonly SnapshotStore _store;
    private readonly IClientAddressSource _clientAddresses;
    private readonly AcmeChallengeStore? _challenges;
    private readonly ProofOfWorkRuntime _proofOfWork;

    /// <param name="store">The live snapshot.</param>
    /// <param name="clientAddresses">Resolves the client address.</param>
    /// <param name="challenges">The HTTP-01 answers; without it the gate answers no challenge.</param>
    /// <param name="proofOfWork">The proof-of-work key, clock and counters for the verify endpoint; null: a keyless runtime.</param>
    public GateMiddleware(
        SnapshotStore store, IClientAddressSource clientAddresses, AcmeChallengeStore? challenges = null, ProofOfWorkRuntime? proofOfWork = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clientAddresses);
        _store = store;
        _clientAddresses = clientAddresses;
        _challenges = challenges;
        _proofOfWork = proofOfWork ?? ProofOfWorkRuntime.Unavailable;
    }

    /// <summary>For <c>app.Use(gate.InvokeAsync)</c>.</summary>
    public Task InvokeAsync(HttpContext context, RequestDelegate next) => InvokeCoreAsync(context, next);

    /// <summary>
    /// <see cref="Decide"/>, then <see cref="FinishAsync"/>; a deferred policy is awaited first and the chain resumed after
    /// it, so the last header stage and the answer see the final decision.
    /// </summary>
    private Task InvokeCoreAsync(HttpContext context, RequestDelegate next)
    {
        var decision = Decide(context);
        return decision.Action == GateAction.AwaitPolicy ? CompleteDeferredAsync(context, next) : FinishAsync(context, decision, next);
    }

    /// <summary>
    /// The last header stage (the response <c>X-Request-Id</c>), then <see cref="CompleteAsync"/>. The ID's header value is the
    /// one string a request materialises; it is built here, once, and shared with the upstream header and the access log, so
    /// <see cref="Decide"/> stays at 0 B.
    /// </summary>
    private Task FinishAsync(HttpContext context, GateDecision decision, RequestDelegate next)
    {
        var state = context.Features.Get<IGateFeature>()?.State;
        if (state is not null)
        {
            var headers = state.Host is { } host ? host.ResponseHeaders : state.Snapshot?.DefaultResponseHeaders ?? [];
            ResponseHeaders.ApplyStage(context, headers, ResponseHeaderStage.AfterDecision, state, [], applyRequestId: true);
        }

        if (decision.Action == GateAction.VerifyProofOfWork && state?.Host is { } verifyHost)
        {
            return ProofOfWorkEndpoint.HandleAsync(context, verifyHost, _proofOfWork);
        }

        return CompleteAsync(context, decision, next);
    }

    /// <summary>Awaits the deferred policy and finishes the request with the verdict the rest of the chain gives.</summary>
    internal async Task CompleteDeferredAsync(HttpContext context, RequestDelegate next)
    {
        var decision = await ResumeAsync(context).ConfigureAwait(false);
        await FinishAsync(context, decision, next).ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits the policy that deferred, then carries on from the policy after it through the rest of the same chain and
    /// everything that follows it (the host's redirect and fixed answers, route matching, the route's chain, the endpoint),
    /// exactly as <see cref="Decide"/> would have had the verdict been synchronous. A later policy may defer too.
    /// </summary>
    private static async ValueTask<GateDecision> ResumeAsync(HttpContext context)
    {
        var state = context.Features.Get<IGateFeature>()!.State;
        while (true)
        {
            var policy = state.DeferredPolicy!;
            var scope = state.DeferredScope;
            var index = state.DeferredIndex;
            state.DeferredPolicy = null;
            var verdict = await policy.CompleteAsync(context, state).ConfigureAwait(false);
            if (verdict.Action != PolicyAction.Continue)
            {
                state.Outcome = policy.Outcome;
            }

            var decision = verdict.Action switch
            {
                PolicyAction.Continue => ContinueAfter(context, state, scope, index + 1),
                PolicyAction.Respond => GateDecision.Respond(verdict.Response!, verdict.RetryAfter),
                _ => GateDecision.Abort,
            };
            if (decision.Action != GateAction.AwaitPolicy)
            {
                return decision;
            }
        }
    }

    private static GateDecision ContinueAfter(HttpContext context, GateRequestState state, PolicyScope scope, int next)
    {
        var snapshot = state.Snapshot!;
        var tracer = default(NoGateTracer);
        RequestView view = new(context, state);
        if (scope == PolicyScope.Route)
        {
            return DecideRoute(context, snapshot, state.Route!, state, next, in view, tracer);
        }

        var host = state.Host!;
        return Stops(PolicyScope.Host, host.Policies, next, in view, tracer, out var stopped)
            ? stopped
            : DecideAfterHostChain(context, snapshot, host, state, in view, tracer);
    }

    internal GateDecision Decide(HttpContext context) => Decide(context, _store.Current, _clientAddresses, _challenges, _proofOfWork, default(NoGateTracer));

    /// <summary><see cref="Decide{TTracer}(HttpContext, RuntimeSnapshot, IClientAddressSource, AcmeChallengeStore, ProofOfWorkRuntime, TTracer)"/> with a keyless runtime (a test that doesn't exercise proof of work).</summary>
    internal static GateDecision Decide<TTracer>(HttpContext context, RuntimeSnapshot snapshot, IClientAddressSource clientAddresses, AcmeChallengeStore? challenges, TTracer tracer)
        where TTracer : struct, IGateTracer =>
        Decide(context, snapshot, clientAddresses, challenges, ProofOfWorkRuntime.Unavailable, tracer);

    /// <summary>
    /// The decision over <paramref name="snapshot"/>, reporting every step to <paramref name="tracer"/>. The one body
    /// production (<see cref="NoGateTracer"/>, which the JIT removes) and the request simulator both run.
    /// </summary>
    internal static GateDecision Decide<TTracer>(
        HttpContext context, RuntimeSnapshot snapshot, IClientAddressSource clientAddresses, AcmeChallengeStore? challenges, ProofOfWorkRuntime proofOfWork, TTracer tracer)
        where TTracer : struct, IGateTracer
    {
        var state = AcquireState(context);
        state.Snapshot = snapshot;
        var client = clientAddresses.Resolve(context, snapshot);
        state.ClientIp = client;
        state.IsHttps = client.Scheme.EffectiveIsHttps(context.Request.IsHttps);
        // The same trust decision the forwarding headers make: a trusted proxy may name the request; anyone else's header
        // is replaced. 0 B: the header value is adopted by reference, or only a counter is taken.
        state.InitRequestId(context.Request, client.PeerTrusted);
        tracer.ClientResolved(context, snapshot, in client, state.IsHttps);

        var answered = TryAnswerAcmeChallenge(challenges, context, out var challenge);
        tracer.AcmeChallenge(context, answered);
        if (answered)
        {
            ApplyHeaders(context, snapshot.DefaultResponseHeaders, ResponseHeaderStage.AfterHostLookup, state, null, tracer);
            return challenge;
        }

        var request = context.Request;
        var hostHeader = request.Headers.Host;
        var matched = HostMatchKind.None;
        var host = hostHeader.Count == 1 ? snapshot.Hosts.Find(hostHeader[0], out matched) : null;
        tracer.HostLookup(hostHeader, host, matched, snapshot.Default);
        if (host is null)
        {
            var fallback = snapshot.Default;
            if (fallback.Action == DefaultHostAction.Close)
            {
                return GateDecision.Abort;
            }

            ApplyHeaders(context, snapshot.DefaultResponseHeaders, ResponseHeaderStage.AfterHostLookup, state, null, tracer);
            return GateDecision.Respond(fallback.Response!);
        }

        state.Host = host;
        ApplyHeaders(context, host.ResponseHeaders, ResponseHeaderStage.AfterHostLookup, state, host, tracer);

        // Before any answer, so a body the gate leaves unread (a redirect, a refusal) can't be drained past the host's limit.
        RequestLimitsGate.ApplyBodyLimit(context, host.RequestLimits.MaxBodySize);

        // Before the TLS check and everything else of the host: a peer that isn't Cloudflare learns nothing from the host,
        // not even a 421. The trust is the resolution's own (the policy's trusted peers, after PROXY protocol), not a
        // second test of the address. Answered or aborted here, the verdict is a constant: 0 B.
        if (host.OriginLockdown != OriginLockdownMode.Off)
        {
            var refused = !client.PeerTrusted;
            tracer.OriginLockdown(context, host, refused);
            if (refused)
            {
                return host.OriginLockdown == OriginLockdownMode.Drop ? GateDecision.Abort : GateDecision.Respond(OriginLockdownResponses.Forbidden);
            }
        }

        // The second line of Authenticated Origin Pulls. Kestrel sets the client certificate on a connection only when the
        // handshake asked for one and accepted it, so its presence is the verdict. Without one: on a TLS connection whose SNI
        // doesn't select this host's TLS entry (a coalesced connection, or no SNI) the handshake never asked, and Cloudflare
        // would present its certificate on a connection of its own, so the answer is 421 like any other coalescing mismatch
        // (the client retries on a fresh connection). Plain HTTP can't carry a certificate, and a connection whose SNI does
        // name the host and still has none has nothing to retry: 403. Before the TLS check, so the request stops at the
        // certificate whichever version or protocol the connection negotiated.
        if (host.AuthenticatedOriginPulls)
        {
            var verified = context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate is not null;
            var misdirected = !verified && request.IsHttps && !SniSelectsEntryOf(context, snapshot, hostHeader);
            tracer.ClientCertificate(context, host, verified, misdirected);
            if (!verified)
            {
                return GateDecision.Respond(misdirected ? snapshot.Misdirected : OriginLockdownResponses.ClientCertificateRequired);
            }
        }

        var connectionIsHttps = request.IsHttps;
        var mismatch = connectionIsHttps
            ? host.Https ? ConnectionTlsCheck.Check(context, host.Tls) : TlsMismatch.HostNotServedOverHttps
            : TlsMismatch.None;
        tracer.TlsCheck(context, host, connectionIsHttps, mismatch);
        if (mismatch != TlsMismatch.None)
        {
            return GateDecision.Respond(snapshot.Misdirected);
        }

        ApplyHeaders(context, host.ResponseHeaders, ResponseHeaderStage.AfterTlsCheck, state, host, tracer);
        RequestView view = new(context, state, TTracer.IsSimulation);
        return Stops(PolicyScope.Host, host.Policies, 0, in view, tracer, out var stopped)
            ? stopped
            : DecideAfterHostChain(context, snapshot, host, state, in view, tracer);
    }

    /// <summary>The decision from the end of the host's policy chain on: a separate method so a deferred host policy can resume here.</summary>
    private static GateDecision DecideAfterHostChain<TTracer>(
        HttpContext context, RuntimeSnapshot snapshot, CompiledHost host, GateRequestState state, in RequestView view, TTracer tracer)
        where TTracer : struct, IGateTracer
    {
        var request = context.Request;

        // After the host chain, so a client it refuses learns nothing from the size of a request; before the redirect, which
        // would otherwise echo a target the host doesn't take, and before anything is routed or read.
        var sizeVerdict = RequestLimitsGate.Check(context, host.RequestLimits, out var size);
        tracer.RequestLimits(host, in size, sizeVerdict);
        if (sizeVerdict != RequestLimitVerdict.Within)
        {
            return GateDecision.Respond(RequestLimitResponses.For(sizeVerdict));
        }

        // The trusted scheme, not the connection's: behind a TLS-terminating trusted proxy the client already uses HTTPS.
        if (host.HttpsRedirect is { } httpsRedirect)
        {
            var redirect = !state.IsHttps;
            tracer.HttpsRedirect(host, state.IsHttps, redirect);
            if (redirect)
            {
                return GateDecision.Respond(httpsRedirect.For(request.Method));
            }
        }

        // After the host chain and the HTTPS redirect, so an allow list still applies and plain HTTP still redirects first
        // (crawlers follow the redirect). Before a fixed answer and route matching, so a Redirect/Respond host can carry a
        // robots.txt too, and the route chain (rate limit, proof of work) never sees the file.
        if (RobotsTargets.IsRequest(request))
        {
            var served = host.RobotsTxt is not null;
            tracer.RobotsTxt(host, served);
            if (served)
            {
                return GateDecision.Respond(host.RobotsTxt!);
            }
        }

        // After robots.txt, which a crawler should still read, and before every answer of the host's own and the route: a host in
        // maintenance is down for all of them. After the host chain, the limits and the redirect, so a refused client is still refused, an
        // oversized request still 413s/414s/431s, and plain HTTP still redirects before it is told to come back.
        var maintenance = host.Maintenance;
        var bypassed = maintenance is not null && maintenance.Bypasses(state.ClientAddress, state.ClientCountry);
        tracer.Maintenance(host, maintenance, bypassed);
        if (maintenance is not null && !bypassed)
        {
            return GateDecision.Respond(maintenance.Response, maintenance.RetryAfter);
        }

        if (host.FixedResponse is { } fixedResponse)
        {
            tracer.FixedResponse(host);
            return GateDecision.Respond(fixedResponse);
        }

        // The proof-of-work verify endpoint, before route matching: the gate answers it for any host where the gate is on
        // for the host or one of its routes, and every other host lets the path fall through as usual. A verify POST is a
        // body to read, so the asynchronous endpoint handles it after this decision (see FinishAsync).
        if (host.ServesProofOfWork && ProofOfWorkTargets.IsVerifyPath(request.Path.Value.AsSpan()))
        {
            if (HttpMethods.IsPost(request.Method))
            {
                tracer.ProofOfWorkVerify(context, host);
                return GateDecision.VerifyProofOfWork;
            }

            return GateDecision.Respond(ProofOfWorkResponses.MethodNotAllowed);
        }

        var route = MatchRoute(host, request.Path.Value, tracer);
        if (route is null)
        {
            return GateDecision.Respond(snapshot.NoRoute);
        }

        state.Route = route;
        if (route.MaxBodySize is { } routeBodyLimit)
        {
            RequestLimitsGate.ApplyBodyLimit(context, routeBodyLimit);
        }

        return DecideRoute(context, snapshot, route, state, 0, in view, tracer);
    }

    /// <summary>The decision from a route's policy chain (starting at policy <paramref name="first"/>) to the endpoint.</summary>
    private static GateDecision DecideRoute<TTracer>(
        HttpContext context, RuntimeSnapshot snapshot, CompiledRoute route, GateRequestState state, int first, in RequestView view, TTracer tracer)
        where TTracer : struct, IGateTracer
    {
        if (Stops(PolicyScope.Route, route.Policies, first, in view, tracer, out var stopped))
        {
            return stopped;
        }

        // After the route's chain (a rate limit or a challenge answers first), before anything reads the body: a body that
        // declares its size over the limit is never forwarded. One without a declared size is Kestrel's to cut off.
        var bodyLimit = route.MaxBodySize ?? state.Host!.RequestLimits.MaxBodySize;
        var declared = context.Request.ContentLength;
        var tooLarge = bodyLimit != RequestLimitBounds.UnlimitedBodySize && declared > bodyLimit;
        tracer.BodyLimit(context, route, bodyLimit, declared, tooLarge);
        if (tooLarge)
        {
            return GateDecision.Respond(RequestLimitResponses.ContentTooLarge);
        }

        var endpoints = snapshot.YarpEndpoints;
        var endpointPresent = (uint)route.Slot < (uint)endpoints.Length;
        tracer.Forward(snapshot, route, endpointPresent);
        if (!endpointPresent)
        {
            return GateDecision.Respond(snapshot.Unavailable);
        }

        context.SetEndpoint(endpoints[route.Slot]);
        // Recorded where the state is at hand, so the completion that carries the decision out needs no feature lookup for it.
        state.Outcome = GateOutcome.Forwarded;
        return GateDecision.Forward;
    }

    /// <summary>
    /// Whether the name in the connection's handshake (empty when the client sent none) leads to the TLS entry the host's own
    /// name does. Only asked of a request that has no verified certificate, so the lookups cost nothing on the allowed path.
    /// </summary>
    private static bool SniSelectsEntryOf(HttpContext context, RuntimeSnapshot snapshot, StringValues hostHeader)
    {
        var sni = context.Features.Get<ITlsHandshakeFeature>()?.HostName ?? string.Empty;
        var own = snapshot.Certificates.Find(HostTable.HostName(hostHeader[0]));
        return own is not null && ReferenceEquals(own, snapshot.Certificates.Find(sni));
    }

    /// <summary>Applies one stage of a host's response-header list and reports it.</summary>
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
        var used = outcomes[..Math.Min(headers.Length, ResponseHeaders.MaxResponseHeaderOperations)];
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
                return ResponseWriter.WriteAsync(context, decision.Response!, decision.RetryAfter);
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

    /// <summary>Runs a chain from policy <paramref name="first"/>; true (with the decision) when a policy responds, drops or defers.</summary>
    private static bool Stops<TTracer>(
        PolicyScope scope, IRequestPolicy[] policies, int first, in RequestView view, TTracer tracer, out GateDecision decision)
        where TTracer : struct, IGateTracer
    {
        for (var i = first; i < policies.Length; i++)
        {
            var policy = policies[i];
            var verdict = policy.Evaluate(in view);
            tracer.PolicyEvaluated(scope, i, policy, in view, verdict);
            switch (verdict.Action)
            {
                case PolicyAction.Continue:
                    continue;
                case PolicyAction.Respond:
                    view.State.Outcome = policy.Outcome;
                    decision = GateDecision.Respond(verdict.Response!, verdict.RetryAfter);
                    return true;
                case PolicyAction.Defer:
                    var state = view.State;
                    state.DeferredPolicy = (IDeferredPolicy)policy;
                    state.DeferredScope = scope;
                    state.DeferredIndex = i;
                    decision = GateDecision.AwaitPolicy;
                    return true;
                default:
                    view.State.Outcome = policy.Outcome;
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
    /// can't be blocked, redirected to HTTPS or answered by the default host. The token is the rest of the
    /// path, compared exactly; anything else (another method, HTTPS, an unknown or nested token) falls through to the
    /// normal pipeline, where it is a request like any other: that is how an upstream that runs its own ACME client
    /// keeps serving its own challenges. 0 B.
    /// </summary>
    private static bool TryAnswerAcmeChallenge(AcmeChallengeStore? challenges, HttpContext context, out GateDecision decision)
    {
        decision = default;
        var request = context.Request;
        if (challenges is null || request.IsHttps || request.Path.Value is not { } path
            || !path.StartsWith(AcmeChallengeStore.PathPrefix, StringComparison.Ordinal)
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            return false;
        }

        if (!challenges.TryGet(path.AsSpan(AcmeChallengeStore.PathPrefix.Length), out var response))
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

        var rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
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
    /// Acquires the pooled state at the top of the public pipeline and resets it for this request. The access log calls
    /// this before the gate so the request's start timestamp covers the whole pipeline; the gate's own
    /// <see cref="AcquireState"/> then reuses the state as it is.
    /// </summary>
    internal static GateRequestState BeginRequest(HttpContext context) => Acquire(context, reset: true);

    /// <summary>
    /// The gate's acquisition: reset only when no earlier stage did, so calling <see cref="Decide"/> twice for one
    /// request (a test, the simulator) still starts from a clean state when the access log isn't above the gate.
    /// </summary>
    private static GateRequestState AcquireState(HttpContext context) => Acquire(context, reset: false);

    /// <summary>
    /// The pooled per-request state. Kestrel keeps <see cref="IPersistentStateFeature"/> on the H1
    /// connection or the pooled H2/H3 stream, so this allocates only for a new connection or stream; without the
    /// feature (non-Kestrel servers) it allocates per request.
    /// </summary>
    private static GateRequestState Acquire(HttpContext context, bool reset)
    {
        GateRequestState state;
        var persistent = context.Features.Get<IPersistentStateFeature>()?.State;
        if (persistent is not null && persistent.TryGetValue(StateKey, out var cached) && cached is GateRequestState reused)
        {
            state = reused;
            // A non-zero start means BeginRequest already reset it for this request; the gate must not wipe the
            // decisions, the ID and the timestamp that are already this request's.
            if (reset || state.RequestStartedTimestamp == 0)
            {
                state.Reset();
            }
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
