using System.Text;
using System.Text.Json;

using Paduraksa.Application.Simulation;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Simulation;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Simulation;

public sealed class SimulationContractTests
{
    [Fact]
    public void ARequestReadsWithDefaultsFromAUrlAndAnIp()
    {
        var request = Deserialize<SimulationRequest>("{\"url\":\"https://app.example.com/a?b=1\",\"clientIp\":\"203.0.113.7\"}");

        Assert.Equal(SimulationArrival.Direct, request.Arrival);
        Assert.Empty(request.Headers);
        Assert.Null(request.Method);
        Assert.Null(request.Tls);
    }

    [Fact]
    public void ARequestWithEveryPartRoundTrips()
    {
        SimulationRequest request = new(
            "https://app.example.com:8443/a?b=1",
            "203.0.113.7",
            "POST",
            [new SimulationHeader("User-Agent", "curl/8"), new SimulationHeader("Cookie", "a=b")],
            SimulationArrival.CloudflareTunnel,
            "127.0.0.1",
            ProxyProtocol: true,
            TransportIp: "192.0.2.1",
            Tls: new SimulationTls("app.example.com", SimulationHttpVersion.Http3, SimulationTlsVersion.Tls13));

        var json = JsonSerializer.Serialize(request, SimulationJsonContext.Default.SimulationRequest);

        Assert.Contains("\"arrival\":\"cloudflareTunnel\"", json, StringComparison.Ordinal);
        Assert.Contains("\"httpVersion\":\"http3\"", json, StringComparison.Ordinal);
        Assert.Equal(request, Deserialize<SimulationRequest>(json) with { Headers = request.Headers }, new RequestComparer());
        Assert.Equal(request.Headers, Deserialize<SimulationRequest>(json).Headers);
    }

    [Fact]
    public void EveryTargetKindRoundTripsWithItsKind()
    {
        var document = Valid();
        SimulationTarget[] targets =
        [
            SimulationTarget.Live,
            new RevisionSimulationTarget(Id<ConfigRevisionId>(3)),
            new DraftDocumentSimulationTarget(document),
            new DraftCommandSimulationTarget(new UpsertHostCommand(document.Hosts[0])),
            new DraftCommandSimulationTarget(new DeleteUpstreamCommand(AppUpstream, 4) { DryRun = true, Message = "try" }),
        ];
        string[] kinds = ["live", "revision", "draftDocument", "draftCommand", "draftCommand"];

        for (var i = 0; i < targets.Length; i++)
        {
            var json = JsonSerializer.Serialize(targets[i], SimulationJsonContext.Default.SimulationTarget);
            Assert.StartsWith($"{{\"kind\":\"{kinds[i]}\"", json, StringComparison.Ordinal);
            var read = JsonSerializer.Deserialize(json, SimulationJsonContext.Default.SimulationTarget)!;
            Assert.Equal(targets[i].GetType(), read.GetType());
        }

        var command = JsonSerializer.Serialize(targets[4], SimulationJsonContext.Default.SimulationTarget);
        Assert.Contains("\"command\":\"deleteUpstream\"", command, StringComparison.Ordinal);
        var back = Assert.IsType<DeleteUpstreamCommand>(Assert.IsType<DraftCommandSimulationTarget>(JsonSerializer.Deserialize(command, SimulationJsonContext.Default.SimulationTarget)).Command);
        Assert.Equal(AppUpstream, back.UpstreamId);
        Assert.Equal(4, back.Version);
        Assert.True(back.DryRun);

        // An unknown kind is not guessed at.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"kind\":\"nowhere\"}", SimulationJsonContext.Default.SimulationTarget));
    }

    [Fact]
    public void AResultRoundTripsWithTypedIdsAndNamedEnums()
    {
        SimulationResult result = new(
            SimulationStatus.Simulated,
            new SimulationSource(SimulationTargetKind.Revision, Id<ConfigRevisionId>(2)),
            new SimulationOutcome(SimulationOutcomeKind.ForwardToUpstream, "forwarded"),
            [new SimulationStep(SimulationStepKind.RouteMatch, SimulationStepVerdict.Passed, "route", [new SimulationFact("path", "/")], AppHost, RootRoute, 0)],
            new SimulationUpstream(AppUpstream, [new SimulationDestination(AppDestination, "http://app", SimulationHealth.Healthy)]),
            Errors: [new ConfigError("/x", "code", "message")],
            Notes: ["note"]);

        var json = JsonSerializer.Serialize(result, SimulationJsonContext.Default.SimulationResult);

        Assert.Contains($"\"host\":\"{AppHost}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"forwardToUpstream\"", json, StringComparison.Ordinal);
        Assert.Contains("\"health\":\"healthy\"", json, StringComparison.Ordinal);
        var read = JsonSerializer.Deserialize(json, SimulationJsonContext.Default.SimulationResult)!;
        Assert.Equal(result.Upstream!.Destinations, read.Upstream!.Destinations);
        Assert.Equal(AppHost, read.Trace[0].Host);
        Assert.Equal(result.Errors, read.Errors);
        Assert.Equal(result.Notes, read.Notes);
    }

    [Fact]
    public void AUrlAndAClientAreEnough()
    {
        var probe = Build(new SimulationRequest("https://App.Example.com/a%2Fb/../c?x=1&y=%20#frag", "203.0.113.7"));

        Assert.True(probe.Https);
        Assert.Equal("app.example.com", probe.Host);
        Assert.Equal("/a%2Fb/../c", probe.Path); // as written: the gate has its own rules for escapes and dot segments
        Assert.Equal("x=1&y=%20", probe.Query);
        Assert.Equal("GET", probe.Method);
        Assert.Equal(ProbeHttpVersion.Http2, probe.HttpVersion);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls13, probe.TlsVersion);
        Assert.Null(probe.ServerName);
        Assert.Equal(ProbeArrival.Direct, probe.Arrival);
        Assert.Equal("203.0.113.7", probe.ClientAddress.ToString());
    }

    [Theory]
    [InlineData("http://app.example.com", "app.example.com", "/", false)]
    [InlineData("http://app.example.com:80/", "app.example.com", "/", false)]
    [InlineData("https://app.example.com:443/x", "app.example.com", "/x", true)]
    [InlineData("https://app.example.com:8443/x", "app.example.com:8443", "/x", true)]
    [InlineData("http://[2001:db8::1]:8080/x", "[2001:db8::1]:8080", "/x", false)]
    [InlineData("http://192.0.2.1/?q", "192.0.2.1", "/", false)]
    public void TheHostHeaderIsTheUrlsAuthorityWithoutADefaultPort(string url, string host, string path, bool https)
    {
        var probe = Build(new SimulationRequest(url, "192.0.2.9"));

        Assert.Equal(host, probe.Host);
        Assert.Equal(path, probe.Path);
        Assert.Equal(https, probe.Https);
        Assert.Equal(https ? ProbeHttpVersion.Http2 : ProbeHttpVersion.Http11, probe.HttpVersion);
    }

    [Fact]
    public void EveryPartOfTheRequestIsCarriedOver()
    {
        var probe = Build(new SimulationRequest(
            "https://app.example.com/",
            "203.0.113.7",
            " POST ",
            [new SimulationHeader("User-Agent", "curl")],
            SimulationArrival.TrustedProxy,
            "10.0.0.1",
            true,
            "192.0.2.1",
            new SimulationTls("other.example.com", SimulationHttpVersion.Http11, SimulationTlsVersion.Tls12)));

        Assert.Equal("POST", probe.Method);
        Assert.Equal([new ProbeHeader("User-Agent", "curl")], probe.Headers);
        Assert.Equal(ProbeArrival.TrustedProxy, probe.Arrival);
        Assert.Equal("10.0.0.1", probe.PeerAddress!.ToString());
        Assert.True(probe.ProxyProtocol);
        Assert.Equal("192.0.2.1", probe.TransportAddress!.ToString());
        Assert.Equal("other.example.com", probe.ServerName);
        Assert.Equal(ProbeHttpVersion.Http11, probe.HttpVersion);
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, probe.TlsVersion);
    }

    [Theory]
    [InlineData("", "203.0.113.7", "/url", SimulationErrorCodes.UrlInvalid)]
    [InlineData("app.example.com/x", "203.0.113.7", "/url", SimulationErrorCodes.UrlInvalid)]
    [InlineData("ftp://app.example.com/", "203.0.113.7", "/url", SimulationErrorCodes.UrlInvalid)]
    [InlineData("https://user@app.example.com/", "203.0.113.7", "/url", SimulationErrorCodes.UrlInvalid)]
    [InlineData("https://app.example.com/a b", "203.0.113.7", "/url", SimulationErrorCodes.UrlInvalid)]
    [InlineData("https://app.example.com/", "not-an-ip", "/clientIp", SimulationErrorCodes.ClientIpInvalid)]
    [InlineData("https://app.example.com/", "fe80::1%eth0", "/clientIp", SimulationErrorCodes.ClientIpInvalid)]
    public void AMalformedRequestIsReportedByField(string url, string client, string path, string code)
    {
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest(url, client), out var probe, out var errors));

        Assert.Null(probe);
        var error = Assert.Single(errors);
        Assert.Equal(path, error.Path);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        SimulationRequest request = new(
            "http://app.example.com/",
            "bad",
            "GE T",
            [new SimulationHeader("Bad Name", "v"), new SimulationHeader("X-Ok", "line\r\nbreak")],
            PeerIp: "bad",
            ProxyProtocol: true,
            TransportIp: "bad",
            Tls: new SimulationTls("x.example.com", SimulationHttpVersion.Http3));

        Assert.False(SimulationRequests.TryBuild(request, out _, out var errors));

        Assert.Equal(
            ["/clientIp", "/peerIp", "/transportIp", "/method", "/headers/0/name", "/headers/1/value", "/tls"],
            errors.Select(e => e.Path));
    }

    [Fact]
    public void TheMethodIsKeptAsWritten()
    {
        Assert.Equal("get", Build(new SimulationRequest("http://app.example.com/", "192.0.2.9", "get")).Method);
        Assert.Equal("PROPFIND", Build(new SimulationRequest("http://app.example.com/", "192.0.2.9", "PROPFIND")).Method);
    }

    [Fact]
    public void HostHeadersAreTheHttp1HostsAndOtherHeadersStay()
    {
        var probe = Build(new SimulationRequest(
            "https://app.example.com/", "192.0.2.9", Headers: [new SimulationHeader("host", "a.test"), new SimulationHeader("User-Agent", "x"), new SimulationHeader("HOST", "b.test")]));

        Assert.Equal(["a.test", "b.test"], probe.HostHeaders);
        Assert.Equal([new ProbeHeader("User-Agent", "x")], probe.Headers);
        Assert.Null(Build(new SimulationRequest("https://app.example.com/", "192.0.2.9")).HostHeaders);
    }

    [Theory]
    [InlineData("TransportIp", "/transportIp")]
    [InlineData("PeerIp", "/peerIp")]
    [InlineData("TlsOnHttp", "/tls")]
    [InlineData("H3OnHttp", "/tls")]
    [InlineData("H2cOnHttp", "/tls")]
    public void AFieldThatWouldChangeNothingIsRefusedNotIgnored(string which, string path)
    {
        var request = which switch
        {
            "TransportIp" => new SimulationRequest("https://app.example.com/", "192.0.2.9", TransportIp: "192.0.2.1"),
            "PeerIp" => new SimulationRequest("https://app.example.com/", "192.0.2.9", PeerIp: "192.0.2.1"),
            "TlsOnHttp" => new SimulationRequest("http://app.example.com/", "192.0.2.9", Tls: new SimulationTls(TlsVersion: SimulationTlsVersion.Tls12)),
            "H3OnHttp" => new SimulationRequest("http://app.example.com/", "192.0.2.9", Tls: new SimulationTls(HttpVersion: SimulationHttpVersion.Http3)),
            _ => new SimulationRequest("http://app.example.com/", "192.0.2.9", Tls: new SimulationTls(HttpVersion: SimulationHttpVersion.Http2)),
        };

        Assert.False(SimulationRequests.TryBuild(request, out _, out var errors));

        Assert.Equal(path, Assert.Single(errors).Path);
    }

    [Theory]
    [InlineData(SimulationArrival.CloudflareProxy, "de", "DE")]
    [InlineData(SimulationArrival.CloudflareTunnel, " T1 ", "T1")]
    [InlineData(SimulationArrival.CloudflareProxy, "xx", "XX")]
    public void ACloudflareArrivalCarriesTheCountryItsHeaderWouldHold(SimulationArrival arrival, string country, string expected)
    {
        var request = new SimulationRequest("https://app.example.com/", "192.0.2.9", Arrival: arrival, Country: country);

        Assert.Equal(expected, Build(request).Country);
        Assert.Equal(country, Deserialize<SimulationRequest>(JsonSerializer.Serialize(request, SimulationJsonContext.Default.SimulationRequest)).Country);
        Assert.Contains("\"country\":", JsonSerializer.Serialize(request, SimulationJsonContext.Default.SimulationRequest), StringComparison.Ordinal);
    }

    [Fact]
    public void NoCountryIsNoHeader() =>
        Assert.Null(Build(new SimulationRequest("https://app.example.com/", "192.0.2.9", Arrival: SimulationArrival.CloudflareProxy)).Country);

    [Theory]
    [InlineData("ZZ")]
    [InlineData("Germany")]
    [InlineData("")]
    [InlineData("D")]
    public void ACountryThatIsNoCodeIsRefused(string country)
    {
        Assert.False(SimulationRequests.TryBuild(
            new SimulationRequest("https://app.example.com/", "192.0.2.9", Arrival: SimulationArrival.CloudflareProxy, Country: country), out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(("/country", SimulationErrorCodes.CountryInvalid), (error.Path, error.Code));
    }

    [Theory]
    [InlineData(SimulationArrival.Direct)]
    [InlineData(SimulationArrival.TrustedProxy)]
    public void ACountryOnAnArrivalCloudflareDoesntTakeIsRefusedNotIgnored(SimulationArrival arrival)
    {
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest("https://app.example.com/", "192.0.2.9", Arrival: arrival, Country: "DE"), out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(("/country", SimulationErrorCodes.Conflicting), (error.Path, error.Code));
        Assert.Contains("CF-IPCountry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePeerIsAllowedBehindAProxyOrWithProxyProtocol()
    {
        Assert.Equal("10.0.0.1", Build(new SimulationRequest("https://app.example.com/", "192.0.2.9", Arrival: SimulationArrival.TrustedProxy, PeerIp: "10.0.0.1")).PeerAddress!.ToString());
        var protocol = Build(new SimulationRequest("https://app.example.com/", "192.0.2.9", PeerIp: "192.0.2.9", ProxyProtocol: true, TransportIp: "192.0.2.1"));
        Assert.True(protocol.ProxyProtocol);
        Assert.Equal("192.0.2.1", protocol.TransportAddress!.ToString());
    }

    [Fact]
    public void KestrelsDefaultLimitsAreEnforced()
    {
        var ok = "http://app.example.com/" + new string('a', SimulationRequests.MaxRequestLineBytes - 100);
        Assert.True(SimulationRequests.TryBuild(new SimulationRequest(ok, "192.0.2.9"), out _, out _));

        // The request line: method, target and version together.
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest("http://app.example.com/" + new string('a', SimulationRequests.MaxRequestLineBytes), "192.0.2.9"), out _, out var line));
        Assert.Equal(SimulationErrorCodes.TooLarge, Assert.Single(line).Code);
        var nearly = "http://app.example.com/" + new string('a', SimulationRequests.MaxRequestLineBytes - 20);
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest(nearly, "192.0.2.9", "OPTIONS" + new string('X', 20)), out _, out _));

        // The number of headers, and their total size.
        SimulationHeader[] many = [.. Enumerable.Range(0, SimulationRequests.MaxHeaderCount + 1).Select(i => new SimulationHeader($"X-{i}", "v"))];
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest("http://app.example.com/", "192.0.2.9", Headers: many), out _, out var count));
        Assert.Equal("/headers", Assert.Single(count).Path);
        Assert.True(SimulationRequests.TryBuild(new SimulationRequest("http://app.example.com/", "192.0.2.9", Headers: many[..SimulationRequests.MaxHeaderCount]), out _, out _));
        SimulationHeader[] heavy = [.. Enumerable.Range(0, 10).Select(i => new SimulationHeader($"X-{i}", new string('v', 4000)))];
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest("http://app.example.com/", "192.0.2.9", Headers: heavy), out _, out var size));
        Assert.Equal(SimulationErrorCodes.TooLarge, Assert.Single(size).Code);

        // The method.
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest("http://app.example.com/", "192.0.2.9", new string('M', SimulationRequests.MaxMethodLength + 1)), out _, out _));
    }

    [Fact]
    public void KestrelsLimitsAreCountedInBytesNotCharacters()
    {
        // The URL: 8,192 UTF-8 bytes in 4,089 characters passes; two more bytes do not.
        var url = "http://a.test/" + new string('é', 4089);
        Assert.Equal(SimulationRequests.MaxRequestLineBytes, Encoding.UTF8.GetByteCount(url));
        Assert.True(SimulationRequests.TryBuild(new SimulationRequest(url, "192.0.2.9"), out _, out _));
        Assert.Equal(SimulationErrorCodes.TooLarge, Assert.Single(Errors("http://a.test/" + new string('é', 4090), "192.0.2.9")).Code);

        // The request line: the same multi-byte path, a 32-character method and the version add up to 8,193 bytes.
        Assert.True(SimulationRequests.TryBuild(
            new SimulationRequest("http://a.test/" + new string('é', 4074), "192.0.2.9", new string('M', SimulationRequests.MaxMethodLength)), out _, out _));
        Assert.Equal(SimulationErrorCodes.TooLarge, Assert.Single(
            Errors("http://a.test/" + new string('é', 4075), "192.0.2.9", new string('M', SimulationRequests.MaxMethodLength))).Code);

        // Headers are counted as UTF-8 bytes on the wire, where 'é' takes two: 16,381 of them plus one ASCII byte make
        // exactly 32,768 bytes with the name and separators, and fit; one more byte does not.
        Assert.True(SimulationRequests.TryBuild(
            new SimulationRequest("http://a.test/", "192.0.2.9", Headers: [new SimulationHeader("X", new string('é', 16381) + "a")]), out _, out _));
        Assert.Equal(SimulationErrorCodes.TooLarge, Assert.Single(
            Errors("http://a.test/", "192.0.2.9", headers: [new SimulationHeader("X", new string('é', 16381) + "ab")])).Code);
    }

    [Theory]
    [InlineData("http://app.example.com/a?b=c\u0001d")]
    [InlineData("http://app.example.com/a?b=c d")]
    [InlineData("http://app.example.com\\evil.test/")]
    [InlineData("http://app.example.com/a\u007fb")]
    public void ControlCharactersInTheQueryAndBackslashesInTheAuthorityAreRefused(string url)
    {
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest(url, "192.0.2.9"), out _, out var errors));

        Assert.Equal("/url", Assert.Single(errors).Path);
    }

    private static GateProbe Build(SimulationRequest request)
    {
        Assert.True(SimulationRequests.TryBuild(request, out var probe, out var errors), string.Join("; ", errors.Select(e => $"{e.Path} {e.Message}")));
        return probe!;
    }

    private static List<ConfigError> Errors(string url, string client, string? method = null, IReadOnlyList<SimulationHeader>? headers = null)
    {
        Assert.False(SimulationRequests.TryBuild(new SimulationRequest(url, client, method, headers ?? []), out _, out var errors));
        return errors;
    }

    private static T Deserialize<T>(string json) =>
        (T)JsonSerializer.Deserialize(json, typeof(T), SimulationJsonContext.Default)!;

    // Records compare their list properties by reference: compare the scalar parts, the lists are asserted apart.
    private sealed class RequestComparer : IEqualityComparer<SimulationRequest>
    {
        public bool Equals(SimulationRequest? x, SimulationRequest? y) =>
            x is not null && y is not null
            && x.Url == y.Url && x.ClientIp == y.ClientIp && x.Method == y.Method && x.Arrival == y.Arrival && x.PeerIp == y.PeerIp
            && x.ProxyProtocol == y.ProxyProtocol && x.TransportIp == y.TransportIp && x.Country == y.Country && Equals(x.Tls, y.Tls);

        public int GetHashCode(SimulationRequest obj) => obj.Url.GetHashCode(StringComparison.Ordinal);
    }
}
