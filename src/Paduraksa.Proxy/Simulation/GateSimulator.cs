using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

using Yarp.ReverseProxy.Configuration;

namespace Paduraksa.Proxy.Simulation;

/// <summary>What the public listeners do before a request exists: their PROXY protocol rules and whether there is a QUIC listener.</summary>
/// <param name="HttpProxyProtocol">The plain-HTTP listener's PROXY protocol settings; null: none.</param>
/// <param name="HttpsProxyProtocol">The HTTPS (TCP) listener's PROXY protocol settings; null: none.</param>
/// <param name="Http3">There is an HTTP/3 listener.</param>
public sealed record ListenerFacts(ProxyProtocolOptions? HttpProxyProtocol, ProxyProtocolOptions? HttpsProxyProtocol, bool Http3);

/// <summary>Puts a <see cref="GateProbe"/> through the real gate over a snapshot, without a network. What this adds
/// is only the stand-ins for what the server does before the gate: the listener's admission (<see
/// cref="ProxyProtocolAdmission"/>, the connection middleware's own decision), the TLS handshake's certificate
/// selection (the same <see cref="TlsHandshakeSelector.Select"/> the listeners call), Kestrel's request checks (a
/// Host it takes, a path it can read, dot segments removed), and the <c>HttpContext</c> with the connection facts the
/// gate reads.</summary>
/// <remarks>
/// Side-effect free: no counter moves, no log line is written, nothing is published or stored, and no key is touched (the
/// certificate table is only consulted for which entry answers a name). Policies are told they are simulated
/// (<see cref="RequestView.IsSimulation"/>) and must not change state. A snapshot that isn't bound to YARP endpoints (a
/// draft or an old revision) is bound to placeholder endpoints first, so a route that would forward says so.
/// </remarks>
public static class GateSimulator
{
    private const int BodyPreviewLimit = 512;
    private const int ClientPort = 40000;

    /// <summary>Simulates <paramref name="probe"/> against <paramref name="snapshot"/>.</summary>
    /// <param name="snapshot">The snapshot to decide with (live, or compiled from a revision or a draft).</param>
    /// <param name="probe">The request.</param>
    /// <param name="challenges">The HTTP-01 answers the gate consults: the live store (challenges are process state, not
    /// config, so every target uses it); null: none is answered.</param>
    /// <param name="listeners">The public listeners' PROXY protocol rules and HTTP/3 availability; null: the listener isn't
    /// modelled, and any connection is let through.</param>
    /// <param name="proofOfWork">The process's proof-of-work key, clock and counters; the live one for a live simulation.
    /// Null: a keyless runtime, so a protected route reports the 503 it would give.</param>
    /// <param name="now">The time the trace's ages are read against (how old the Cloudflare edge ranges are); null: the ages aren't stated.</param>
    public static GateSimulation Run(
        RuntimeSnapshot snapshot, GateProbe probe, AcmeChallengeStore? challenges = null, ListenerFacts? listeners = null, ProofOfWorkRuntime? proofOfWork = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.HttpVersion == ProbeHttpVersion.Http3 && !probe.Https)
        {
            throw new ArgumentException("HTTP/3 only exists over TLS.", nameof(probe));
        }

        foreach (var header in probe.Headers)
        {
            if (string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Host headers go in GateProbe.HostHeaders.", nameof(probe));
            }
        }

        var bound = snapshot.IsBound ? snapshot : snapshot.Bind(1, PlaceholderEndpoints(snapshot));
        List<GateStep> steps = [];
        var peer = probe.PeerAddress ?? DefaultPeer(bound.TrustedProxies, probe);

        if (listeners is not null && Admit(listeners, bound, probe, peer, steps) is { } refused)
        {
            return new GateSimulation(refused, steps, null);
        }

        var tlsVersion = SslProtocols.None;
        var negotiated = probe.HttpVersion;
        var certificateAsked = false;
        if (probe.Https && Handshake(bound, probe, steps, out tlsVersion, out negotiated, out certificateAsked) is { } rejected)
        {
            return new GateSimulation(rejected, steps, null);
        }

        if (listeners is not null && probe.Https && probe.HttpVersion == ProbeHttpVersion.Http3 && QuicLockdown(bound, peer, steps) is { } dropped)
        {
            return new GateSimulation(dropped, steps, null);
        }

        if (CheckRequest(probe, negotiated, steps, out var path, out var hostHeader) is { } bad)
        {
            return new GateSimulation(bad, steps, null);
        }

        var context = BuildContext(bound, probe, peer, negotiated, tlsVersion, certificateAsked, path, hostHeader);
        var decision = GateMiddleware.Decide(context, bound, SimulationClientAddressSource.Instance, challenges, proofOfWork ?? ProofOfWorkRuntime.Unavailable, new RecordingGateTracer(steps, now));
        var outcome = OutcomeOf(decision, context, steps);
        var upstream = outcome.Action == GateOutcomeAction.Forward && context.Features.Get<IGateFeature>()?.State.Route is { } route
            ? route.Admin
                ? new GateUpstream(UpstreamId.Empty, null, [], Admin: true)
                : UpstreamOf(bound, route)
            : null;
        return new GateSimulation(outcome, steps, upstream);
    }

    /// <summary>The YARP cluster ID of <paramref name="route"/> in <paramref name="snapshot"/> (the upstream's ID), if any.</summary>
    internal static string? ClusterOf(RuntimeSnapshot snapshot, CompiledRoute route)
    {
        if ((uint)route.Slot >= (uint)snapshot.RouteIdBySlot.Length)
        {
            return null;
        }

        var routeId = snapshot.RouteIdBySlot[route.Slot].ToString();
        foreach (var candidate in snapshot.Yarp.Routes)
        {
            if (string.Equals(candidate.RouteId, routeId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.ClusterId;
            }
        }

        return null;
    }

    private static GateUpstream? UpstreamOf(RuntimeSnapshot snapshot, CompiledRoute route)
    {
        var clusterId = ClusterOf(snapshot, route);
        if (clusterId is null || !UpstreamId.TryParse(clusterId, out var upstream))
        {
            return null;
        }

        foreach (var cluster in snapshot.Yarp.Clusters)
        {
            if (!string.Equals(cluster.ClusterId, clusterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<GateDestination> destinations = [];
            foreach (var destination in cluster.Destinations ?? new Dictionary<string, DestinationConfig>())
            {
                if (DestinationId.TryParse(destination.Key, out var id))
                {
                    destinations.Add(new GateDestination(id, destination.Value.Address));
                }
            }

            return new GateUpstream(upstream, cluster.LoadBalancingPolicy, destinations);
        }

        return new GateUpstream(upstream, null, []);
    }

    private static Endpoint[] PlaceholderEndpoints(RuntimeSnapshot snapshot)
    {
        var endpoints = new Endpoint[snapshot.RouteIdBySlot.Length];
        for (var i = 0; i < endpoints.Length; i++)
        {
            endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, snapshot.RouteIdBySlot[i].ToString());
        }

        return endpoints;
    }

    /// <summary>The listener's say, before TLS or any byte of the request: PROXY protocol, and whether QUIC exists at all.
    /// Returns the rejection, or null to go on.</summary>
    private static GateOutcome? Admit(ListenerFacts listeners, RuntimeSnapshot snapshot, GateProbe probe, IPAddress peer, List<GateStep> steps)
    {
        var quic = probe.HttpVersion == ProbeHttpVersion.Http3;
        List<GateFact> facts = [new("transport", quic ? "quic" : "tcp")];
        string? refusal = null;
        if (quic)
        {
            if (!listeners.Http3)
            {
                refusal = "there is no HTTP/3 listener, so nothing answers over QUIC";
            }
            else if (probe.ProxyProtocol)
            {
                refusal = "QUIC has no PROXY protocol: the client is the socket's address";
            }
        }
        else
        {
            var options = probe.Https ? listeners.HttpsProxyProtocol : listeners.HttpProxyProtocol;
            var socket = probe.ProxyProtocol ? probe.TransportAddress ?? IPAddress.Loopback : peer;
            facts.Add(new("proxyProtocolListener", options is null ? "off" : "required"));
            facts.Add(new("socketPeer", socket.ToString()));
            refusal = ProxyProtocolAdmission.Check(options, IpKey.FromIPAddress(socket), probe.ProxyProtocol) switch
            {
                ConnectionAdmission.HeaderUnexpected => "the listener has no PROXY protocol, so the PROXY header is read as a broken HTTP request",
                ConnectionAdmission.HeaderMissing => "the listener requires a PROXY protocol header and the peer sends none: the connection is dropped",
                ConnectionAdmission.UntrustedSource => $"the listener requires PROXY protocol and the socket peer {socket} isn't one of its trusted sources: the connection is dropped unread",
                _ => null,
            };
        }

        // After PROXY protocol, so the address is the one the gate will see, and over TLS only: the plain-HTTP listener leaves
        // the decision to the gate, because an ACME validation may come from anywhere. QUIC is checked after its handshake's
        // name selection instead, as the handshake callback does (see QuicLockdown).
        if (refusal is null && probe.Https && !quic)
        {
            refusal = LockdownRefusal(snapshot, peer, facts);
        }

        if (refusal is not null)
        {
            steps.Add(new GateStep(GateStepKind.Connection, GateStepVerdict.Stopped, refusal, facts));
            return new GateOutcome(GateOutcomeAction.Abort, GateReason.ConnectionRejected);
        }

        steps.Add(new GateStep(GateStepKind.Connection, GateStepVerdict.Passed, "the listener lets the connection through", facts));
        return null;
    }

    /// <summary>Why the connection stage drops <paramref name="peer"/> before TLS, or null: every host is locked down and the
    /// peer isn't a trusted one. Adds what it looked at to <paramref name="facts"/>.</summary>
    private static string? LockdownRefusal(RuntimeSnapshot snapshot, IPAddress peer, List<GateFact> facts)
    {
        if (!snapshot.LockdownAtConnection)
        {
            return null;
        }

        var trusted = snapshot.TrustedProxies.IsTrustedPeer(IpKey.FromIPAddress(peer));
        facts.Add(new("lockdownAtConnection", "yes"));
        facts.Add(new("peerTrusted", trusted ? "yes" : "no"));
        if (trusted)
        {
            return null;
        }

        var what = snapshot.TrustedProxies.Mode == TrustedProxyMode.CloudflareTunnel ? "the configured cloudflared address" : "a trusted Cloudflare peer";
        return $"every host is locked down to Cloudflare and {peer} is not {what}: the connection is dropped before TLS";
    }

    /// <summary>The QUIC handshake callback selects the options for the name first (an unknown name or a host without HTTP/3
    /// is rejected there) and only then runs the connection gate, so the lockdown of an untrusted peer comes after the
    /// handshake step, not before it.</summary>
    private static GateOutcome? QuicLockdown(RuntimeSnapshot snapshot, IPAddress peer, List<GateStep> steps)
    {
        List<GateFact> facts = [new("transport", "quic")];
        if (LockdownRefusal(snapshot, peer, facts) is not { } refusal)
        {
            return null;
        }

        steps.Add(new GateStep(GateStepKind.Connection, GateStepVerdict.Stopped, refusal, facts));
        return new GateOutcome(GateOutcomeAction.Abort, GateReason.ConnectionRejected);
    }

    /// <summary>What Kestrel does before the gate runs: select the certificate entry for the SNI and settle the TLS
    /// version and ALPN.</summary>
    private static GateOutcome? Handshake(
        RuntimeSnapshot snapshot, GateProbe probe, List<GateStep> steps, out SslProtocols tlsVersion, out ProbeHttpVersion negotiated, out bool certificateAsked)
    {
        certificateAsked = false;
        var quic = probe.HttpVersion == ProbeHttpVersion.Http3;
        var serverName = probe.ServerName ?? DefaultServerName(probe.Host);
        var selection = TlsHandshakeSelector.Select(snapshot.Certificates, serverName, quic);
        List<GateFact> facts =
        [
            new("serverName", serverName.Length == 0 ? "(none)" : serverName),
            new("transport", quic ? "quic" : "tcp"),
            new("match", TraceNames.Of(selection.Match)),
        ];
        tlsVersion = quic ? SslProtocols.Tls13 : probe.TlsVersion;
        negotiated = probe.HttpVersion;

        if (selection.Options is not { } options)
        {
            var why = selection.Rejection == TlsRejectionReason.Http3Disabled
                ? $"HTTP/3 is disabled for '{serverName}': the QUIC handshake is rejected"
                : serverName.Length == 0
                    ? "no SNI and no default certificate: the handshake is rejected"
                    : $"no certificate for '{serverName}' and no default certificate: the handshake is rejected";
            steps.Add(new GateStep(GateStepKind.TlsHandshake, GateStepVerdict.Stopped, why, facts));
            return new GateOutcome(GateOutcomeAction.Abort, GateReason.HandshakeRejected);
        }

        if (!quic && (options.EnabledSslProtocols & tlsVersion) == 0)
        {
            facts.Add(new("clientTlsVersion", TraceNames.Of(tlsVersion)));
            facts.Add(new("offeredVersions", TraceNames.Of(options.EnabledSslProtocols)));
            steps.Add(new GateStep(
                GateStepKind.TlsHandshake,
                GateStepVerdict.Stopped,
                $"the certificate entry for '{serverName}' needs {TraceNames.Of(options.EnabledSslProtocols)}, the client offers only {TraceNames.Of(tlsVersion)}: the handshake fails",
                facts));
            return new GateOutcome(GateOutcomeAction.Abort, GateReason.HandshakeRejected);
        }

        if (!quic && options.ClientCertificateRequired && !PresentsClientCertificate(probe))
        {
            facts.Add(new("clientCertificate", "required, none presented"));
            steps.Add(new GateStep(
                GateStepKind.TlsHandshake,
                GateStepVerdict.Stopped,
                $"the entry for '{serverName}' requires Cloudflare's client certificate (Authenticated Origin Pulls) and the client presents none that its CA signed: the handshake fails",
                facts));
            return new GateOutcome(GateOutcomeAction.Abort, GateReason.HandshakeRejected);
        }

        certificateAsked = !quic && options.ClientCertificateRequired;
        var certificate = selection.Entry!.Certificate.Id;
        var summary = $"{(selection.Match == CertificateMatchKind.Default ? "the default certificate" : "a certificate")}"
                      + $"{(certificate.IsEmpty ? string.Empty : $" {certificate}")} answers '{serverName}' ({TraceNames.Of(selection.Match)})";
        if (!certificate.IsEmpty)
        {
            facts.Add(new("certificate", certificate.ToString()));
        }

        if (!quic && negotiated == ProbeHttpVersion.Http2 && !Offers(options, SslApplicationProtocol.Http2))
        {
            negotiated = ProbeHttpVersion.Http11;
            facts.Add(new("alpn", "http/1.1"));
            summary += "; it doesn't offer h2, so the connection falls back to HTTP/1.1";
        }
        else
        {
            facts.Add(new("alpn", quic ? "h3" : negotiated == ProbeHttpVersion.Http2 ? "h2" : "http/1.1"));
        }

        facts.Add(new("tlsVersion", TraceNames.Of(tlsVersion)));
        steps.Add(new GateStep(GateStepKind.TlsHandshake, GateStepVerdict.Passed, summary, facts));
        return null;
    }

    private static bool PresentsClientCertificate(GateProbe probe) =>
        probe.ClientCertificate ?? probe.Arrival == ProbeArrival.CloudflareProxy;

    private static bool Offers(SslServerAuthenticationOptions options, SslApplicationProtocol protocol)
    {
        foreach (var offered in options.ApplicationProtocols ?? [])
        {
            if (offered == protocol)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The SNI a browser would send for this host: the name, unless it is an IP literal.</summary>
    private static string DefaultServerName(string host)
    {
        var name = HostTable.HostName(host);
        return name.IsEmpty || name[0] == '[' || IPAddress.TryParse(name, out _) ? string.Empty : name.ToString();
    }

    /// <summary>
    /// What Kestrel checks and rewrites before the gate sees the request: HTTP/1.1 needs exactly one valid <c>Host</c>;
    /// HTTP/2 and HTTP/3 use <c>:authority</c> (a <c>Host</c> header is ignored); the path is percent-decoded and its dot
    /// segments removed. A bad Host is Kestrel's own 400, so the gate never runs.
    /// </summary>
    private static GateOutcome? CheckRequest(GateProbe probe, ProbeHttpVersion version, List<GateStep> steps, out string path, out string hostHeader)
    {
        path = string.Empty;
        hostHeader = probe.Host;
        List<GateFact> facts = [new("path", probe.Path)];
        string? refusal = null;
        if (version == ProbeHttpVersion.Http11)
        {
            var hosts = probe.HostHeaders ?? [probe.Host];
            facts.Add(new("hostHeaders", hosts.Count.ToString(CultureInfo.InvariantCulture)));
            if (hosts.Count != 1)
            {
                refusal = hosts.Count == 0
                    ? "an HTTP/1.1 request without a Host header: Kestrel answers 400"
                    : $"an HTTP/1.1 request with {hosts.Count} Host headers: Kestrel answers 400";
            }
            else
            {
                hostHeader = hosts[0];
            }
        }
        else
        {
            facts.Add(new("authority", probe.Host));
            if (probe.HostHeaders is { Count: > 0 } ignored)
            {
                facts.Add(new("ignoredHostHeaders", ignored.Count.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (refusal is null && !IsValidHost(hostHeader))
        {
            refusal = $"'{hostHeader}' isn't a valid host: Kestrel answers 400";
        }

        if (refusal is null)
        {
            path = RemoveDotSegments(DecodePath(probe.Path));
            facts.Add(new("decodedPath", path));
        }

        if (refusal is not null)
        {
            steps.Add(new GateStep(GateStepKind.Request, GateStepVerdict.Stopped, refusal, facts));
            return new GateOutcome(GateOutcomeAction.Respond, GateReason.RejectedByServer, StatusCodes.Status400BadRequest);
        }

        steps.Add(new GateStep(GateStepKind.Request, GateStepVerdict.Passed, "the server accepts the request line and Host", facts));
        return null;
    }

    /// <summary>A Host (or <c>:authority</c>) value Kestrel takes: a reg-name or IP literal with an optional port, no user
    /// info, no path or whitespace characters.</summary>
    private static bool IsValidHost(string host)
    {
        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || "-._~[]:%!$&'()*+,;=".Contains(c, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static DefaultHttpContext BuildContext(
        RuntimeSnapshot snapshot, GateProbe probe, IPAddress peer, ProbeHttpVersion version, SslProtocols tlsVersion, bool certificateAsked, string path, string hostHeader)
    {
        DefaultHttpContext context = new();
        var request = context.Request;
        request.Method = probe.Method;
        request.Scheme = probe.Https ? "https" : "http";
        request.Protocol = version switch
        {
            ProbeHttpVersion.Http3 => HttpProtocol.Http3,
            ProbeHttpVersion.Http2 => HttpProtocol.Http2,
            _ => HttpProtocol.Http11,
        };
        request.Path = new PathString(path);
        request.QueryString = string.IsNullOrEmpty(probe.Query) ? QueryString.Empty : new QueryString("?" + probe.Query);
        if (context.Features.Get<IHttpRequestFeature>() is { } feature)
        {
            feature.RawTarget = probe.Path + (string.IsNullOrEmpty(probe.Query) ? string.Empty : "?" + probe.Query);
        }

        foreach (var header in probe.Headers)
        {
            request.Headers.Append(header.Name, header.Value);
        }

        request.Headers.Host = hostHeader;
        AddClientHeader(request.Headers, snapshot.TrustedProxies, probe);

        var address = IpKey.FromIPAddress(peer);
        var transport = probe.ProxyProtocol ? IpKey.FromIPAddress(probe.TransportAddress ?? IPAddress.Loopback) : address;
        ClientInfo info = new(
            address,
            ClientPort,
            probe.ProxyProtocol ? ConnectionAddressSource.ProxyProtocol : ConnectionAddressSource.Socket,
            transport,
            ClientPort);
        context.Features.Set<IClientInfoFeature>(info);
        context.Connection.RemoteIpAddress = probe.ProxyProtocol ? (probe.TransportAddress ?? IPAddress.Loopback) : peer;
        if (probe.Https)
        {
            context.Features.Set<ITlsHandshakeFeature>(new SimulatedTlsHandshake(tlsVersion, probe.ServerName ?? DefaultServerName(probe.Host)));

            // What Kestrel sets once a handshake that asked for a certificate verified it. The simulated handshake that got here
            // has already failed where one was required and absent, so a presented one is a verified one: the CA's own
            // certificate stands in for what Cloudflare sent. A connection whose entry never asked has none, whatever the
            // client could have sent (a coalesced one, or no SNI).
            if (certificateAsked && PresentsClientCertificate(probe))
            {
                context.Features.Set<ITlsConnectionFeature>(new SimulatedTlsConnection(OriginPullCa.Default[0]));
            }
        }

        return context;
    }

    /// <summary>What the proxy in front would send: the client in the header its mode reads, unless the probe set one.</summary>
    private static void AddClientHeader(IHeaderDictionary headers, TrustedProxyPolicy policy, GateProbe probe)
    {
        var name = probe.Arrival switch
        {
            ProbeArrival.TrustedProxy => policy.Mode == TrustedProxyMode.Generic ? policy.ForwardingHeader : TrustedProxyPolicy.XForwardedFor,
            ProbeArrival.CloudflareProxy or ProbeArrival.CloudflareTunnel => CloudflareHeaders.ConnectingIp,
            _ => null,
        };
        if (name is not null && !headers.ContainsKey(name))
        {
            headers[name] = probe.ClientAddress.ToString();
        }

        if (probe.Country is { } country && probe.Arrival is ProbeArrival.CloudflareProxy or ProbeArrival.CloudflareTunnel
            && !headers.ContainsKey(CloudflareHeaders.IpCountry))
        {
            headers[CloudflareHeaders.IpCountry] = country;
        }
    }

    /// <summary>Direct: the client. Behind a proxy: an address the snapshot trusts as that kind of proxy, else loopback.</summary>
    private static IPAddress DefaultPeer(TrustedProxyPolicy policy, GateProbe probe)
    {
        if (probe.Arrival == ProbeArrival.Direct)
        {
            return probe.ClientAddress;
        }

        var matchesMode = probe.Arrival switch
        {
            ProbeArrival.TrustedProxy => policy.Mode == TrustedProxyMode.Generic,
            ProbeArrival.CloudflareProxy => policy.Mode == TrustedProxyMode.CloudflareProxy,
            _ => policy.Mode == TrustedProxyMode.CloudflareTunnel,
        };
        if (matchesMode)
        {
            foreach (var (first, _) in policy.TrustedPeers.GetRanges())
            {
                return first.ToIPAddress();
            }
        }

        return IPAddress.Loopback;
    }

    private static GateOutcome OutcomeOf(GateDecision decision, DefaultHttpContext context, List<GateStep> steps)
    {
        var reason = ReasonOf(steps);
        switch (decision.Action)
        {
            case GateAction.Forward:
                return new GateOutcome(GateOutcomeAction.Forward, GateReason.Forwarded);
            case GateAction.Respond:
                var response = decision.Response!;
                // A page-only answer (the challenge) has no text to describe: set its status, and let BodyOf read the
                // rendered page. Every other answer goes through the plain header path, redirects included.
                if (response.HasPlainBody)
                {
                    ResponseWriter.ApplyHeaders(context, response, decision.RetryAfter);
                }
                else
                {
                    context.Response.StatusCode = response.StatusCode;
                }

                var location = response.StatusCode / 100 == 3 && context.Response.Headers.Location.Count > 0
                    ? context.Response.Headers.Location.ToString()
                    : null;
                return new GateOutcome(GateOutcomeAction.Respond, reason, response.StatusCode, location, BodyOf(response));
            // A probe carries no body, and the real gate refuses a post without a challenge and solution with 403.
            case GateAction.VerifyProofOfWork:
                return new GateOutcome(GateOutcomeAction.Respond, GateReason.ProofOfWork, StatusCodes.Status403Forbidden);
            default:
                return new GateOutcome(GateOutcomeAction.Abort, reason);
        }
    }

    private static string? BodyOf(PrecomputedResponse response)
    {
        if (response.Body.Length == 0 || response.Redirect is not null || response.Location.Count > 0
            || !response.ContentType.ToString().StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(response.Body, 0, Math.Min(response.Body.Length, BodyPreviewLimit));
        return response.Body.Length > BodyPreviewLimit ? text + "..." : text;
    }

    private static bool AnswerIs421(GateStep step)
    {
        foreach (var fact in step.Facts)
        {
            if (fact is { Name: "answer", Value: "421" })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The step that stopped the request names the reason.</summary>
    private static GateReason ReasonOf(List<GateStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Verdict == GateStepVerdict.Stopped)
            {
                return step.Kind switch
                {
                    GateStepKind.Connection => GateReason.ConnectionRejected,
                    GateStepKind.TlsHandshake => GateReason.HandshakeRejected,
                    GateStepKind.Request => GateReason.RejectedByServer,
                    GateStepKind.AcmeChallenge => GateReason.AcmeChallenge,
                    GateStepKind.HostMatch => GateReason.DefaultHost,
                    GateStepKind.OriginLockdown => GateReason.OriginLockdown,
                    GateStepKind.ClientCertificate => AnswerIs421(step) ? GateReason.MisdirectedConnection : GateReason.ClientCertificate,
                    GateStepKind.TlsPolicy => GateReason.MisdirectedConnection,
                    GateStepKind.HostPolicy => GateReason.HostPolicy,
                    GateStepKind.HttpsRedirect => GateReason.HttpsRedirect,
                    GateStepKind.RobotsTxt => GateReason.RobotsTxt,
                    GateStepKind.FixedResponse => GateReason.FixedHost,
                    GateStepKind.ProofOfWork => GateReason.ProofOfWork,
                    GateStepKind.RouteMatch => GateReason.NoRoute,
                    GateStepKind.RoutePolicy => GateReason.RoutePolicy,
                    _ => GateReason.Unavailable,
                };
            }
        }

        return GateReason.Forwarded;
    }

    /// <summary>The path the way Kestrel hands it to the gate: percent-escapes decoded as UTF-8, except <c>%2F</c>
    /// and <c>%5C</c>, which stay encoded (the route matching view reads them as separators; <see
    /// cref="RoutePath"/>). Escapes that aren't valid UTF-8 stay as written: Kestrel doesn't reject them (checked
    /// against the real server).</summary>
    internal static string DecodePath(string raw)
    {
        if (raw.IndexOf('%', StringComparison.Ordinal) < 0)
        {
            return raw;
        }

        StringBuilder text = new(raw.Length);
        List<byte> pending = [];
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '%' && i + 2 < raw.Length && TryHex(raw[i + 1], raw[i + 2], out var value) && value is not (0x2F or 0x5C))
            {
                pending.Add(value);
                i += 2;
                continue;
            }

            Flush(text, pending);
            text.Append(raw[i]);
        }

        Flush(text, pending);
        return text.ToString();

        static void Flush(StringBuilder to, List<byte> bytes)
        {
            if (bytes.Count == 0)
            {
                return;
            }

            // Valid UTF-8 is decoded; a byte that isn't part of a valid sequence stays as the escape it was.
            Span<byte> span = [.. bytes];
            while (!span.IsEmpty)
            {
                if (Rune.DecodeFromUtf8(span, out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
                {
                    to.Append(rune.ToString());
                    span = span[consumed..];
                }
                else
                {
                    to.Append('%').Append(span[0].ToString("X2", CultureInfo.InvariantCulture));
                    span = span[1..];
                }
            }

            bytes.Clear();
        }
    }

    /// <summary>Removes <c>.</c> and <c>..</c> segments (RFC 3986 §5.2.4) as Kestrel's path normalizer does after
    /// decoding, so the gate (and the ACME token) see the same path the real server gives it.</summary>
    internal static string RemoveDotSegments(string path)
    {
        if (!path.Contains("/.", StringComparison.Ordinal))
        {
            return path;
        }

        List<string> output = [];
        var segments = path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            var last = i == segments.Length - 1;
            if (segment == ".")
            {
                if (last)
                {
                    output.Add(string.Empty);
                }
            }
            else if (segment == "..")
            {
                if (output.Count > 0)
                {
                    output.RemoveAt(output.Count - 1);
                }

                if (last)
                {
                    output.Add(string.Empty);
                }
            }
            else
            {
                output.Add(segment);
            }
        }

        return "/" + string.Join('/', output);
    }

    private static bool TryHex(char high, char low, out byte value)
    {
        value = 0;
        if (!char.IsAsciiHexDigit(high) || !char.IsAsciiHexDigit(low))
        {
            return false;
        }

        value = (byte)((Convert.ToInt32(high.ToString(), 16) << 4) | Convert.ToInt32(low.ToString(), 16));
        return true;
    }

    private sealed class SimulatedTlsConnection(X509Certificate2 clientCertificate) : ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = clientCertificate;

        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
    }

    // The interface still carries the members SslStream obsoleted; nothing here reads them.
#pragma warning disable SYSLIB0058
    private sealed class SimulatedTlsHandshake(SslProtocols protocol, string serverName) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol { get; } = protocol;

        public TlsCipherSuite? NegotiatedCipherSuite => null;

        public string HostName => serverName;

        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.None;

        public int CipherStrength => 0;

        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.None;

        public int HashStrength => 0;

        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;

        public int KeyExchangeStrength => 0;
    }
#pragma warning restore SYSLIB0058
}

/// <summary>The client-address resolution the simulator runs: the server's own code, without its counters and its log line.</summary>
internal sealed class SimulationClientAddressSource : IClientAddressSource
{
    public static SimulationClientAddressSource Instance { get; } = new();

    public ClientIpResolution Resolve(HttpContext context, RuntimeSnapshot snapshot) =>
        TrustedProxyClientAddressSource.ResolveCore(context, snapshot, counters: null, logger: null);
}

/// <summary>Stable, camelCase names for trace facts: the wire names, not the internal enum and type names.</summary>
internal static class TraceNames
{
    public static string Of<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return name switch
        {
            "IPv6Literal" => "ipv6Literal",
            _ => char.ToLowerInvariant(name[0]) + name[1..],
        };
    }

    public static string Of(SslProtocols protocols) => protocols switch
    {
        SslProtocols.Tls12 => "tls1.2",
        SslProtocols.Tls13 => "tls1.3",
        SslProtocols.Tls12 | SslProtocols.Tls13 => "tls1.2 or tls1.3",
        _ => "none",
    };
}
