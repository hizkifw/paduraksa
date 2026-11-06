using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Snapshot;

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
/// allocation-free: one snapshot read, per-request state from <see cref="IPersistentStateFeature"/>, client address,
/// host, HTTPS redirect, Redirect/Respond hosts, route, policy chain, and finally
/// <see cref="EndpointHttpContextExtensions.SetEndpoint"/> with YARP's endpoint from the same snapshot, so the
/// <c>UseRouting</c> that follows skips matching. Writing a response happens afterwards, in <see cref="CompleteAsync"/>.
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

    /// <summary>The synchronous decision. Never writes to the response; sets the endpoint when forwarding.</summary>
    internal GateDecision Decide(HttpContext context)
    {
        RuntimeSnapshot snapshot = _store.Current;
        GateRequestState state = AcquireState(context);
        state.Snapshot = snapshot;
        state.ClientAddress = _clientAddresses.GetClientAddress(context);

        HttpRequest request = context.Request;
        StringValues hostHeader = request.Headers.Host;
        CompiledHost? host = hostHeader.Count == 1 ? snapshot.Hosts.Find(hostHeader[0]) : null;
        if (host is null)
        {
            DefaultHostBehaviour fallback = snapshot.Default;
            return fallback.Action == DefaultHostAction.Close ? GateDecision.Abort : GateDecision.Respond(fallback.Response!);
        }

        state.Host = host;
        if (host.HttpsRedirect is { } httpsRedirect && !request.IsHttps)
        {
            return GateDecision.Respond(httpsRedirect.For(request.Method));
        }

        if (host.FixedResponse is { } fixedResponse)
        {
            return GateDecision.Respond(fixedResponse);
        }

        CompiledRoute? route = host.Match(request.Path.Value);
        if (route is null)
        {
            return GateDecision.Respond(snapshot.NoRoute);
        }

        state.Route = route;
        RequestView view = new(context, state);
        foreach (IRequestPolicy policy in route.Policies)
        {
            PolicyDecision decision = policy.Evaluate(in view);
            switch (decision.Action)
            {
                case PolicyAction.Continue:
                    continue;
                case PolicyAction.Respond:
                    return GateDecision.Respond(decision.Response!);
                default:
                    return GateDecision.Abort;
            }
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
