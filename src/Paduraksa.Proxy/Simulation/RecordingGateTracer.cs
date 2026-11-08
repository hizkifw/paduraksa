using System.Security.Authentication;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Proxy.Simulation;

/// <summary>
/// The simulator's tracer (PLAN §7.7): writes each step the gate reports as a <see cref="GateStep"/>. It only reads; the
/// gate's decision is the same one production makes with <see cref="NoGateTracer"/>. Cold path: allocates freely.
/// </summary>
internal readonly struct RecordingGateTracer(List<GateStep> steps) : IGateTracer
{
    public static bool IsSimulation => true;

    public void ClientResolved(HttpContext context, RuntimeSnapshot snapshot, in ClientIpResolution client, bool isHttps)
    {
        TrustedProxyPolicy policy = snapshot.TrustedProxies;
        IpKey peer = default;
        string peerKind = "socket";
        if (context.Features.Get<IClientInfoFeature>()?.ClientInfo is { } info)
        {
            peer = info.Address;
            peerKind = info.IsFromProxyProtocol ? "PROXY protocol" : "socket";
        }
        else if (context.Connection.RemoteIpAddress is { } remote)
        {
            peer = IpKey.FromIPAddress(remote);
        }

        string? header = HeaderOf(client.Source, policy);
        if (header is null && client.IsFallback)
        {
            // The peer was used because the header it should have sent was absent or didn't parse.
            header = policy.Mode == TrustedProxyMode.Generic ? policy.ForwardingHeader : CloudflareHeaders.ConnectingIp;
        }

        List<GateFact> facts =
        [
            new("peer", $"{peer} ({peerKind})"),
            new("trustedProxyMode", TraceNames.Of(policy.Mode)),
            new("peerTrusted", Flag(client.PeerTrusted)),
            new("client", client.Address.ToString()),
            new("clientSource", TraceNames.Of(client.Source)),
            new("outcome", TraceNames.Of(client.Outcome)),
        ];
        if (header is not null)
        {
            facts.Add(new("header", header));
            facts.Add(new("headerValue", context.Request.Headers[header].ToString()));
        }

        facts.Add(new("cloudflareHeadersTrusted", Flag(client.CloudflareHeadersTrusted)));
        facts.Add(new("schemeSource", TraceNames.Of(client.Scheme.Source)));
        facts.Add(new("scheme", isHttps ? "https" : "http"));

        string summary = client.Outcome switch
        {
            ClientIpOutcome.PeerNotTrusted when policy.Mode == TrustedProxyMode.Off => $"client {client.Address}: the connection's peer (no trusted proxies are configured)",
            ClientIpOutcome.PeerNotTrusted => $"client {client.Address}: the connection's peer; {peer} is not a trusted proxy, so forwarding headers are ignored",
            ClientIpOutcome.FromHeader => $"client {client.Address}: from {header ?? "a header"}, sent by trusted proxy {peer}",
            ClientIpOutcome.HeaderMissing => $"client {client.Address}: the peer; trusted proxy {peer} sent no usable client-IP header",
            _ => $"client {client.Address}: the peer; trusted proxy {peer} sent a client-IP header that doesn't parse",
        };
        steps.Add(new GateStep(GateStepKind.ClientAddress, GateStepVerdict.Passed, summary, facts));
    }

    public void AcmeChallenge(HttpContext context, bool answered)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        bool challenge = !context.Request.IsHttps && path.StartsWith(AcmeChallengeStore.PathPrefix, StringComparison.Ordinal);
        GateStepVerdict verdict = answered ? GateStepVerdict.Stopped : challenge ? GateStepVerdict.Passed : GateStepVerdict.Skipped;
        string summary = answered
            ? "answered the ACME HTTP-01 challenge with its key authorization"
            : challenge
                ? "an ACME HTTP-01 challenge path, but nothing answers it (no challenge is waiting for this token, or it isn't a GET or HEAD): handled like any request"
                : "not an ACME HTTP-01 challenge request (plain HTTP under /.well-known/acme-challenge/)";
        List<GateFact> facts = [];
        if (challenge)
        {
            facts.Add(new("token", path[AcmeChallengeStore.PathPrefix.Length..]));
            facts.Add(new("method", context.Request.Method));
        }

        steps.Add(new GateStep(GateStepKind.AcmeChallenge, verdict, summary, facts));
    }

    public void HostLookup(StringValues hostHeader, CompiledHost? host, HostMatchKind match, DefaultHostBehaviour fallback)
    {
        List<GateFact> facts = [new("hostHeader", hostHeader.ToString()), new("hostHeaderCount", hostHeader.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        if (host is not null)
        {
            facts.Add(new("match", TraceNames.Of(match)));
            facts.Add(new("kind", TraceNames.Of(host.Kind)));
            steps.Add(new GateStep(
                GateStepKind.HostMatch,
                GateStepVerdict.Passed,
                $"host {host.Id} matched by {Describe(match)}",
                facts,
                host.Id));
            return;
        }

        string why = hostHeader.Count == 1 ? $"no host takes '{hostHeader[0]}'" : $"the request has {hostHeader.Count} Host headers (exactly one is required)";
        string answer = fallback.Action == DefaultHostAction.Close
            ? "the default host behaviour closes the connection"
            : $"the default host behaviour answers {fallback.Response!.StatusCode}";
        facts.Add(new("defaultHost", fallback.Action == DefaultHostAction.Close ? "close" : $"respond {fallback.Response!.StatusCode}"));
        steps.Add(new GateStep(GateStepKind.HostMatch, GateStepVerdict.Stopped, $"{why}: {answer}", facts));
    }

    public void TlsCheck(HttpContext context, CompiledHost host, bool https, TlsMismatch mismatch)
    {
        if (!https)
        {
            steps.Add(new GateStep(GateStepKind.TlsPolicy, GateStepVerdict.Skipped, "plain HTTP: no TLS settings to check", [], host.Id));
            return;
        }

        TlsPolicy tls = host.Tls;
        List<GateFact> facts =
        [
            new("servedOverHttps", Flag(host.Https)),
            new("minimumVersion", tls.MinimumVersion == TlsMinimumVersion.Tls13 ? "tls1.3" : "tls1.2"),
            new("tcpAlpn", TraceNames.Of(tls.TcpAlpn)),
            new("http3", Flag(tls.Http3Enabled)),
            new("protocol", context.Request.Protocol),
        ];
        if (context.Features.Get<ITlsHandshakeFeature>() is { } handshake && !HttpProtocol.IsHttp3(context.Request.Protocol))
        {
            facts.Add(new("tlsVersion", TraceNames.Of(handshake.Protocol)));
        }

        string summary = mismatch switch
        {
            TlsMismatch.None => $"the {context.Request.Protocol} connection satisfies the host's TLS settings",
            TlsMismatch.HostNotServedOverHttps => "the host isn't served over HTTPS (no certificate covers it): 421",
            TlsMismatch.Http3Disabled => "the request came over HTTP/3, which the host doesn't allow: 421",
            TlsMismatch.Http2NotOffered => "the request came over HTTP/2, but the host offers http/1.1 only: 421",
            _ => "the host requires TLS 1.3 and this connection negotiated less: 421",
        };
        steps.Add(new GateStep(
            GateStepKind.TlsPolicy,
            mismatch == TlsMismatch.None ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
            summary,
            facts,
            host.Id));
    }

    public void PolicyEvaluated(PolicyScope scope, int index, IRequestPolicy policy, in RequestView request, PolicyDecision verdict)
    {
        bool stopped = verdict.Action != PolicyAction.Continue;
        List<GateFact> facts = [new("policy", policy.Name), new("verdict", TraceNames.Of(verdict.Action))];
        if (verdict.Response is { } response)
        {
            facts.Add(new("status", response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        steps.Add(new GateStep(
            scope == PolicyScope.Host ? GateStepKind.HostPolicy : GateStepKind.RoutePolicy,
            stopped ? GateStepVerdict.Stopped : GateStepVerdict.Passed,
            $"{policy.Name}: {policy.Explain(in request, verdict)}",
            facts,
            request.State.Host?.Id,
            request.State.Route?.Id,
            index));
    }

    public void HttpsRedirect(CompiledHost host, bool isHttps, bool redirected) =>
        steps.Add(new GateStep(
            GateStepKind.HttpsRedirect,
            redirected ? GateStepVerdict.Stopped : GateStepVerdict.Passed,
            redirected ? "the host forces HTTPS and the request is plain HTTP: redirect to HTTPS" : "the host forces HTTPS and the request already is",
            [new("scheme", isHttps ? "https" : "http")],
            host.Id));

    public void FixedResponse(CompiledHost host) =>
        steps.Add(new GateStep(
            GateStepKind.FixedResponse,
            GateStepVerdict.Stopped,
            host.Kind == HostKind.Redirect ? "a redirect host: it answers every request with its redirect" : "a respond host: it answers every request with its fixed response",
            [new("kind", TraceNames.Of(host.Kind))],
            host.Id));

    public void RouteMatch(CompiledHost host, ReadOnlySpan<char> path, ReadOnlySpan<char> canonicalPath, CompiledRoute? route)
    {
        string raw = path.ToString();
        string canonical = canonicalPath.ToString();
        List<GateFact> facts = [new("path", raw), new("matchedPath", canonical)];
        if (route is null)
        {
            steps.Add(new GateStep(GateStepKind.RouteMatch, GateStepVerdict.Stopped, $"no route of the host takes '{canonical}': 404", facts, host.Id));
            return;
        }

        if (route.Admin)
        {
            // The admin host's implicit route has no user route ID and takes every path (PLAN §3.1).
            facts.Add(new("admin", "yes"));
            facts.Add(new("match", TraceNames.Of(route.MatchKind)));
            steps.Add(new GateStep(
                GateStepKind.RouteMatch,
                GateStepVerdict.Passed,
                $"the admin route takes '{canonical}': every path is forwarded to the admin app",
                facts,
                host.Id));
            return;
        }

        facts.Add(new("route", route.Id.ToString()));
        facts.Add(new("routePath", route.Path));
        facts.Add(new("match", TraceNames.Of(route.MatchKind)));
        facts.Add(new("position", Array.IndexOf(host.Routes, route).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        steps.Add(new GateStep(
            GateStepKind.RouteMatch,
            GateStepVerdict.Passed,
            $"route {route.Id} ({TraceNames.Of(route.MatchKind)} '{route.Path}') takes '{canonical}'",
            facts,
            host.Id,
            route.Id));
    }

    public void Forward(RuntimeSnapshot snapshot, CompiledRoute route, bool endpointPresent)
    {
        List<GateFact> facts = [new("slot", route.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("endpoint", Flag(endpointPresent))];
        if (route.Admin)
        {
            facts.Add(new("cluster", YarpConfig.AdminClusterId));
            facts.Add(new("target", "admin"));
            steps.Add(new GateStep(
                GateStepKind.Upstream,
                endpointPresent ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
                endpointPresent
                    ? "forwarded to the admin app through its internal listener"
                    : "the admin route has no YARP endpoint in this snapshot: 503",
                facts,
                null,
                RouteId.Empty));
            return;
        }

        string? cluster = GateSimulator.ClusterOf(snapshot, route);
        if (cluster is not null)
        {
            facts.Add(new("cluster", cluster));
        }

        steps.Add(new GateStep(
            GateStepKind.Upstream,
            endpointPresent ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
            endpointPresent
                ? $"forwarded to upstream {cluster ?? "(none)"}; load balancing picks the destination per request"
                : "the route has no YARP endpoint in this snapshot: 503",
            facts,
            null,
            route.Id));
    }

    private static string Flag(bool value) => value ? "yes" : "no";

    private static string Describe(HostMatchKind match) => match switch
    {
        HostMatchKind.Exact => "an exact name",
        HostMatchKind.Wildcard => "a wildcard name",
        _ => "its IPv6 literal",
    };

    private static string? HeaderOf(ClientIpSource source, TrustedProxyPolicy policy) => source switch
    {
        ClientIpSource.ForwardingHeader => policy.ForwardingHeader,
        ClientIpSource.CfConnectingIp => CloudflareHeaders.ConnectingIp,
        ClientIpSource.TrueClientIp => CloudflareHeaders.TrueClientIp,
        _ => null,
    };
}
