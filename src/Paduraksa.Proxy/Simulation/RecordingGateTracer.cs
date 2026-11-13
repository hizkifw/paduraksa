using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Simulation;

/// <summary>
/// The simulator's tracer: writes each step the gate reports as a <see cref="GateStep"/>. It only reads; the
/// gate's decision is the same one production makes with <see cref="NoGateTracer"/>. Cold path: allocates freely.
/// </summary>
internal readonly struct RecordingGateTracer(List<GateStep> steps, DateTimeOffset? now = null) : IGateTracer
{
    public static bool IsSimulation => true;

    public void ClientResolved(HttpContext context, RuntimeSnapshot snapshot, in ClientIpResolution client, bool isHttps)
    {
        var policy = snapshot.TrustedProxies;
        var peer = PeerOf(context, out var peerKind);

        var header = HeaderOf(client.Source, policy);
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

        if (snapshot.EdgeRanges is { } edge)
        {
            facts.Add(new("edgeRanges", DescribeEdgeRanges(edge, now)));
        }

        facts.Add(new("cloudflareHeadersTrusted", Flag(client.CloudflareHeadersTrusted)));
        if (context.Request.Headers[CloudflareHeaders.IpCountry] is { Count: > 0 } sent)
        {
            // The header as sent, and what the gate made of it: nothing, unless a trusted Cloudflare peer sent it.
            facts.Add(new("countryHeader", sent.ToString()));
            facts.Add(new("country", client.CloudflareHeadersTrusted ? client.Country.ToString() : "ignored"));
        }

        facts.Add(new("schemeSource", TraceNames.Of(client.Scheme.Source)));
        facts.Add(new("scheme", isHttps ? "https" : "http"));

        var summary = client.Outcome switch
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
        var path = context.Request.Path.Value ?? string.Empty;
        var challengePath = path.StartsWith(AcmeChallengeStore.PathPrefix, StringComparison.Ordinal);

        // Three outcomes: answered; a challenge path nothing answered, which falls through to the pipeline like any
        // request (say why); or no challenge path at all.
        var why = challengePath && !answered
            ? context.Request.IsHttps
                ? "the request is HTTPS, and an HTTP-01 challenge is only answered over plain HTTP"
                : !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                    ? $"the request is a {context.Request.Method}, and only GET and HEAD are answered"
                    : "no challenge is waiting for its token"
            : null;

        var verdict = answered ? GateStepVerdict.Stopped : challengePath ? GateStepVerdict.Passed : GateStepVerdict.Skipped;
        var summary = answered
            ? "answered the ACME HTTP-01 challenge with its key authorization"
            : challengePath
                ? $"an ACME HTTP-01 challenge path, but nothing answers it ({why}): handled like any request"
                : $"not an ACME HTTP-01 challenge path (nothing under {AcmeChallengeStore.PathPrefix})";
        List<GateFact> facts = [];
        if (challengePath)
        {
            facts.Add(new("token", path[AcmeChallengeStore.PathPrefix.Length..]));
            facts.Add(new("method", context.Request.Method));
        }

        if (why is not null)
        {
            facts.Add(new("reason", why));
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

        var why = hostHeader.Count == 1 ? $"no host takes '{hostHeader[0]}'" : $"the request has {hostHeader.Count} Host headers (exactly one is required)";
        var answer = fallback.Action == DefaultHostAction.Close
            ? "the default host behaviour closes the connection"
            : $"the default host behaviour answers {fallback.Response!.StatusCode}";
        facts.Add(new("defaultHost", fallback.Action == DefaultHostAction.Close ? "close" : $"respond {fallback.Response!.StatusCode}"));
        steps.Add(new GateStep(GateStepKind.HostMatch, GateStepVerdict.Stopped, $"{why}: {answer}", facts));
    }

    /// <summary>
    /// One step lists every response header the answer carries and every one it doesn't: the stages report as they run,
    /// and a later stage's report extends the step the earlier one opened (a 421, which stops before the second stage,
    /// therefore lists only what was applied).
    /// </summary>
    public void ResponseHeaders(HttpContext context, CompiledHost? host, ReadOnlySpan<ResponseHeaderOperation> operations, ReadOnlySpan<HeaderOutcome> outcomes)
    {
        List<GateFact> facts = [];
        // A hand-built list past the gate's buffer has outcomes only for its head: report what it has rather than throw.
        for (var i = 0; i < operations.Length && i < outcomes.Length; i++)
        {
            if (outcomes[i] == HeaderOutcome.None)
            {
                continue;
            }

            facts.Add(Describe(context, operations[i], outcomes[i]));
        }

        if (facts.Count == 0)
        {
            return;
        }

        IReadOnlyList<GateFact> all = facts;
        var existing = steps.FindLastIndex(step => step is { Kind: GateStepKind.ResponseHeaders } && step.Host == host?.Id);
        if (existing >= 0)
        {
            all = [.. steps[existing].Facts, .. facts];
        }

        GateStep opened = new(
            GateStepKind.ResponseHeaders,
            GateStepVerdict.Passed,
            host is null
                ? "the global defaults' response headers, which a name no host serves and an ACME answer follow"
                : "the host's response headers",
            all,
            host?.Id);
        if (existing >= 0)
        {
            // The later stage extends the step the earlier one opened, in its place: the trace keeps the order the gate ran in.
            steps[existing] = opened;
        }
        else
        {
            steps.Add(opened);
        }
    }

    /// <summary>One header as a fact: the value it carries, or why the answer doesn't carry it.</summary>
    private static GateFact Describe(HttpContext context, in ResponseHeaderOperation operation, HeaderOutcome outcome)
    {
        var name = operation.Name;
        return outcome switch
        {
            HeaderOutcome.Sent => new(name, operation.Value.ToString()),
            HeaderOutcome.Replaced => new(name, Http.ResponseHeaders.IsRequestId(operation)
                ? (context.Features.Get<IGateFeature>()?.State.RequestIdText() ?? string.Empty)
                : operation.Value.ToString()),
            HeaderOutcome.Deferred => new(name, "sent when the response is written: " + (context.Features.Get<IGateFeature>()?.State.RequestIdText() ?? string.Empty)),
            HeaderOutcome.Removed => new(name, "removed: " + (operation.Reason ?? "whatever the upstream sends is dropped")),
            HeaderOutcome.LeftToUpstream => new(name, "not sent: " + (operation.Reason ?? "the upstream's own passes through")),
            HeaderOutcome.NotSent => new(name, "not sent: " + (operation.Reason ?? "the host doesn't have it")),
            HeaderOutcome.PlainHttp => new(name, "not sent: the request isn't HTTPS"),
            HeaderOutcome.ExemptName => new(name, "not sent: an IP address or a loopback name must not be remembered"),
            _ => new(name, "not sent: HTTP/3 already is the transport"),
        };
    }

    public void OriginLockdown(HttpContext context, CompiledHost host, bool refused)
    {
        // The peer, not the client: the question is who is connected, whatever header they carry.
        var peer = PeerOf(context, out _);
        var mode = TraceNames.Of(host.OriginLockdown);
        var answer = host.OriginLockdown == OriginLockdownMode.Drop ? "the connection is dropped" : "403";
        List<GateFact> facts = [new("lockdown", mode), new("peerTrusted", Flag(!refused))];

        // What a trusted peer is depends on the mode: Cloudflare's edge ranges, or the cloudflared address that was configured.
        var tunnel = context.Features.Get<IGateFeature>()?.State.Snapshot?.TrustedProxies.Mode == TrustedProxyMode.CloudflareTunnel;
        steps.Add(new GateStep(
            GateStepKind.OriginLockdown,
            refused ? GateStepVerdict.Stopped : GateStepVerdict.Passed,
            refused
                ? $"peer {peer} is not {(tunnel ? "the configured cloudflared address" : "a Cloudflare edge address")}: refused ({answer})"
                : $"peer {peer} is {(tunnel ? "the configured cloudflared address" : "a trusted Cloudflare edge address")}: allowed",
            facts,
            host.Id));
    }

    public void ClientCertificate(HttpContext context, CompiledHost host, bool verified, bool misdirected)
    {
        var answer = verified ? "allowed" : misdirected ? "421" : "403";
        var summary = verified
            ? "the connection carries Cloudflare's client certificate, verified by the handshake: allowed"
            : misdirected
                ? "the host requires Cloudflare's client certificate (Authenticated Origin Pulls), and this connection was negotiated for another name, so the handshake never asked for one: 421, so the client retries on a connection of its own"
                : context.Request.IsHttps
                    ? "the host requires Cloudflare's client certificate (Authenticated Origin Pulls) and this connection has none: refused (403)"
                    : "the host requires Cloudflare's client certificate (Authenticated Origin Pulls), which plain HTTP can't carry: refused (403)";
        steps.Add(new GateStep(
            GateStepKind.ClientCertificate,
            verified ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
            summary,
            [new("authenticatedOriginPulls", "required"), new("clientCertificate", Flag(verified)), new("answer", answer)],
            host.Id));
    }

    public void TlsCheck(HttpContext context, CompiledHost host, bool https, TlsMismatch mismatch)
    {
        if (!https)
        {
            steps.Add(new GateStep(GateStepKind.TlsPolicy, GateStepVerdict.Skipped, "plain HTTP: no TLS settings to check", [], host.Id));
            return;
        }

        var tls = host.Tls;
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

        var summary = mismatch switch
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
        var stopped = verdict.Action != PolicyAction.Continue;
        List<GateFact> facts = [new("policy", policy.Name), new("verdict", TraceNames.Of(verdict.Action))];
        if (verdict.Response is { } response)
        {
            facts.Add(new("status", response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (verdict.RetryAfter is { } retryAfter)
        {
            facts.Add(new("retryAfter", retryAfter));
        }

        if (policy is IPolicyTraceFacts traced)
        {
            traced.AddFacts(in request, verdict, facts);
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

    public void RequestLimits(CompiledHost host, in RequestSize size, RequestLimitVerdict verdict)
    {
        var limits = host.RequestLimits;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        List<GateFact> facts =
        [
            new("urlBytes", size.UrlBytes.ToString(inv)),
            new("maxUrlLength", limits.MaxUrlLength.ToString(inv)),
        ];

        // The headers weren't read at all when the target already decided.
        if (verdict != RequestLimitVerdict.UrlTooLong)
        {
            facts.Add(new("headerCount", size.HeaderCount.ToString(inv)));
            facts.Add(new("maxHeaderCount", limits.MaxHeaderCount.ToString(inv)));
        }

        if (size.HeaderBytes >= 0)
        {
            facts.Add(new("headerBytes", size.HeaderBytes.ToString(inv)));
            facts.Add(new("maxHeaderBytes", limits.MaxHeaderBytes.ToString(inv)));
        }

        var summary = verdict switch
        {
            RequestLimitVerdict.UrlTooLong => $"the request target is {SizeText(size.UrlBytes)}, over the host's limit of {SizeText(limits.MaxUrlLength)}: 414",
            RequestLimitVerdict.TooManyHeaders => $"{size.HeaderCount} headers, limit {limits.MaxHeaderCount}: 431",
            RequestLimitVerdict.HeadersTooLarge => $"the headers take {SizeText(size.HeaderBytes)}, over the host's limit of {SizeText(limits.MaxHeaderBytes)}: 431",
            _ => $"{size.HeaderCount} headers ({SizeText(size.HeaderBytes)}) and a {SizeText(size.UrlBytes)} target, within the host's limits of {limits.MaxHeaderCount} headers, {SizeText(limits.MaxHeaderBytes)} and {SizeText(limits.MaxUrlLength)}",
        };
        if (verdict != RequestLimitVerdict.Within)
        {
            facts.Add(new("answer", RequestLimitResponses.For(verdict).StatusCode.ToString(inv)));
        }

        steps.Add(new GateStep(
            GateStepKind.RequestLimits,
            verdict == RequestLimitVerdict.Within ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
            summary,
            facts,
            host.Id));
    }

    /// <summary>Reported only for a request that has a body to speak of: one with a declared length, or a chunked one.</summary>
    public void BodyLimit(HttpContext context, CompiledRoute route, long limit, long? declared, bool refused)
    {
        var request = context.Request;
        var streamed = declared is null && request.Headers.ContainsKey("Transfer-Encoding");
        if (declared is null or 0 && !streamed)
        {
            return;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var unlimited = limit == RequestLimitBounds.UnlimitedBodySize;
        var limitText = unlimited ? "no limit" : $"limit {SizeText(limit)}";
        List<GateFact> facts =
        [
            new("maxBodySize", limit.ToString(inv)),
            new("limitFrom", route.MaxBodySize is null ? "host" : "route"),
        ];
        if (declared is { } length)
        {
            facts.Add(new("contentLength", length.ToString(inv)));
        }
        else
        {
            facts.Add(new("contentLength", "none (chunked)"));
        }

        if (refused)
        {
            facts.Add(new("answer", "413"));
        }

        var summary = refused
            ? $"body {SizeText(declared!.Value)} declared, {limitText}: 413"
            : declared is { } within
                ? $"body {SizeText(within)} declared, {limitText}"
                : unlimited
                    ? "a body with no declared length, and no limit"
                    : $"a body with no declared length, {limitText}: the server cuts it off with 413 once it passes the limit";
        steps.Add(new GateStep(
            GateStepKind.RequestLimits,
            refused ? GateStepVerdict.Stopped : GateStepVerdict.Passed,
            summary,
            facts,
            context.Features.Get<IGateFeature>()?.State.Host?.Id,
            route.Id.IsEmpty ? null : route.Id));
    }

    /// <summary>A size in the largest binary unit that holds it exactly ("10 MiB"), else in bytes.</summary>
    private static string SizeText(long bytes)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes > 0)
        {
            foreach (var (unit, name) in new[] { (1L << 30, "GiB"), (1L << 20, "MiB"), (1L << 10, "KiB") })
            {
                if (bytes % unit == 0)
                {
                    return $"{(bytes / unit).ToString(inv)} {name}";
                }
            }
        }

        return $"{bytes.ToString(inv)} bytes";
    }

    public void HttpsRedirect(CompiledHost host, bool isHttps, bool redirected) =>
        steps.Add(new GateStep(
            GateStepKind.HttpsRedirect,
            redirected ? GateStepVerdict.Stopped : GateStepVerdict.Passed,
            redirected ? "the host forces HTTPS and the request is plain HTTP: redirect to HTTPS" : "the host forces HTTPS and the request already is",
            [new("scheme", isHttps ? "https" : "http")],
            host.Id));

    public void RobotsTxt(CompiledHost host, bool served)
    {
        var mode = TraceNames.Of(host.RobotsMode);
        steps.Add(new GateStep(
            GateStepKind.RobotsTxt,
            served ? GateStepVerdict.Stopped : GateStepVerdict.Passed,
            served
                ? $"the gate served the host's managed robots.txt ({mode})"
                : $"robots.txt is not managed on this host ({mode}): it goes on to {(host.Kind == HostKind.Proxy || host.Kind == HostKind.Admin ? "the upstream" : "the host's fixed answer")}",
            [new("mode", mode), new("served", Flag(served))],
            host.Id));
    }

    public void Maintenance(CompiledHost host, CompiledMaintenance? maintenance, bool bypassed)
    {
        if (maintenance is null)
        {
            steps.Add(new GateStep(GateStepKind.Maintenance, GateStepVerdict.Skipped, "the host isn't in maintenance", [new("maintenance", "off")], host.Id));
            return;
        }

        var list = maintenance.Bypass?.Name;
        List<GateFact> facts =
        [
            new("maintenance", "on"),
            new("status", maintenance.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("retryAfter", maintenance.RetryAfter ?? "none"),
            new("bypassList", list ?? "none"),
            new("bypassed", Flag(bypassed)),
        ];
        steps.Add(new GateStep(
            GateStepKind.Maintenance,
            bypassed ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
            bypassed
                ? $"the host is in maintenance, and the client's address is allowed by the bypass list '{list}': it goes on"
                : list is null
                    ? $"the host is in maintenance: answered {maintenance.Status} with the maintenance page"
                    : $"the host is in maintenance and the bypass list '{list}' doesn't allow the client's address: answered {maintenance.Status} with the maintenance page",
            facts,
            host.Id));
    }

    public void ProofOfWorkVerify(HttpContext context, CompiledHost host) =>
        steps.Add(new GateStep(
            GateStepKind.ProofOfWork,
            GateStepVerdict.Stopped,
            "the proof-of-work verify endpoint: a solved challenge gets 303 and the token; a simulated request posts no form, so it gets 403",
            [new("path", ProofOfWorkTargets.VerifyPath), new("difficulty", host.ProofOfWork.Difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture))],
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
        var raw = path.ToString();
        var canonical = canonicalPath.ToString();
        List<GateFact> facts = [new("path", raw), new("matchedPath", canonical)];
        if (route is null)
        {
            steps.Add(new GateStep(GateStepKind.RouteMatch, GateStepVerdict.Stopped, $"no route of the host takes '{canonical}': 404", facts, host.Id));
            return;
        }

        if (route.Admin)
        {
            // The admin host's implicit route has no user route ID and takes every path.
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

    public void Forward(HttpContext context, RuntimeSnapshot snapshot, CompiledRoute route, bool endpointPresent)
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

        var cluster = GateSimulator.ClusterOf(snapshot, route);
        if (cluster is not null)
        {
            facts.Add(new("cluster", cluster));
        }

        var hostSent = GateSimulator.HostSentOf(snapshot, route, cluster, context.Request.Host.Value ?? string.Empty);
        facts.Add(new("hostHeaderMode", route.UpstreamHost.Mode.ToString()));
        facts.Add(new("hostSent", hostSent));
        steps.Add(new GateStep(
            GateStepKind.Upstream,
            endpointPresent ? GateStepVerdict.Passed : GateStepVerdict.Stopped,
            endpointPresent
                ? $"forwarded to upstream {cluster ?? "(none)"} with Host: {hostSent}; load balancing picks the destination per request"
                : "the route has no YARP endpoint in this snapshot: 503",
            facts,
            null,
            route.Id));
    }

    private static string DescribeEdgeRanges(EdgeRangeList edge, DateTimeOffset? now)
    {
        var which = edge.Origin switch
        {
            EdgeRangeOrigin.BuiltIn => "built-in list",
            EdgeRangeOrigin.Fetched => "fetched list",
            _ => "configured set",
        };
        if (edge.Origin == EdgeRangeOrigin.Configured)
        {
            return which;
        }

        var text = $"{which}, {edge.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} ranges, fetched {edge.FetchedAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";
        return now is { } at
            ? $"{text} ({Math.Max(0, (int)(at - edge.FetchedAt).TotalDays).ToString(System.Globalization.CultureInfo.InvariantCulture)} days ago)"
            : text;
    }

    /// <summary>The connection's peer as the gate resolves it: the PROXY protocol source when the connection has one, else the socket.</summary>
    private static IpKey PeerOf(HttpContext context, out string kind)
    {
        kind = "socket";
        if (context.Features.Get<IClientInfoFeature>()?.ClientInfo is { } info)
        {
            kind = info.IsFromProxyProtocol ? "PROXY protocol" : "socket";
            return info.Address;
        }

        return context.Connection.RemoteIpAddress is { } remote ? IpKey.FromIPAddress(remote) : default;
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
