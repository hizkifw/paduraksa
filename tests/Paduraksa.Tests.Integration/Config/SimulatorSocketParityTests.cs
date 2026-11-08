using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Integration.Proxy;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>
/// The simulator against the real server over real sockets (PLAN §7.7): what Kestrel does with a malformed Host, a dot-segment
/// path, a TLS 1.2 client or an h2 client to a name that offers only http/1.1 is written down here as what the socket
/// answered, not derived from the simulator's own rules, and the simulation must say the same.
/// </summary>
public sealed class SimulatorSocketParityTests
{
    private const string Token = "tok-en_1";
    private const string KeyAuthorization = "tok-en_1.thumbprint";

    private static async Task<PaduraksaHost> StartAsync(CancellationToken ct)
    {
        PaduraksaHost host = TestHost.Create();
        await host.StartAsync(ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("app", "app.test", "app-body")), ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("strict", "strict.test", "strict-body") with { Settings = new HostSettings { Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls13 } } }), ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("h1", "h1.test", "h1-body") with { Settings = new HostSettings { Tls = new TlsSettings { TcpAlpn = TcpAlpn.Http11Only } } }), ct);
        host.Shared.Challenges.Publish(Token, KeyAuthorization);
        return host;
    }

    private static int Status(string response)
    {
        // "HTTP/1.1 400 Bad Request ...", or nothing when the server dropped the connection.
        return response.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) ? int.Parse(response.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    private static Task<SimulationResult> SimulateAsync(PaduraksaHost host, SimulationRequest request, CancellationToken ct) =>
        host.Shared.Simulator.SimulateAsync(SimulationTarget.Live, request, Origin, ct);

    [Fact]
    public async Task ADuplicateOrInvalidHostOnHttp1IsA400BeforeTheGate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = await StartAsync(ct);

        // The server: 400 for two Hosts, and for a Host that isn't one. The default host (404) never gets to answer.
        Assert.Equal(400, Status(await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: app.test\r\nHost: app.test\r\nConnection: close\r\n\r\n"))));
        Assert.Equal(400, Status(await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: a@b\r\nConnection: close\r\n\r\n"))));
        Assert.Equal(400, Status(await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nConnection: close\r\n\r\n"))));
        Assert.Equal(200, Status(await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: app.test\r\nConnection: close\r\n\r\n"))));

        // The simulation says the same.
        SimulationResult duplicate = await SimulateAsync(host, new SimulationRequest("http://app.test/", "203.0.113.7", Headers: [new SimulationHeader("Host", "app.test"), new SimulationHeader("Host", "app.test")]), ct);
        Assert.Equal(SimulationOutcomeKind.RejectedByServer, duplicate.Outcome!.Kind);
        Assert.Equal(400, duplicate.Outcome.StatusCode);
        SimulationResult invalid = await SimulateAsync(host, new SimulationRequest("http://app.test/", "203.0.113.7", Headers: [new SimulationHeader("Host", "a@b")]), ct);
        Assert.Equal(SimulationOutcomeKind.RejectedByServer, invalid.Outcome!.Kind);
        SimulationResult fine = await SimulateAsync(host, new SimulationRequest("http://app.test/", "203.0.113.7"), ct);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, fine.Outcome!.Kind);
        Assert.Equal(200, fine.Outcome.StatusCode);

        // No Host at all can't be said in a request DTO: the probe can.
        GateSimulation none = GateSimulator.Run(host.Shared.SnapshotStore.Current, new GateProbe { Host = "app.test", HostHeaders = [], ClientAddress = IPAddress.Loopback });
        Assert.Equal(GateReason.RejectedByServer, none.Outcome.Reason);
        Assert.Equal(400, none.Outcome.StatusCode);
    }

    [Fact]
    public async Task ADotSegmentPathReachesTheAcmeTokenAsTheServerSeesIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = await StartAsync(ct);
        string path = $"/x/../.well-known/./acme-challenge/{Token}";

        // The server normalises before the gate: the challenge is answered, on any Host.
        string real = await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: nobody.test\r\nConnection: close\r\n\r\n"));
        Assert.Equal(200, Status(real));
        Assert.EndsWith(KeyAuthorization, real, StringComparison.Ordinal);

        SimulationResult simulated = await SimulateAsync(host, new SimulationRequest($"http://nobody.test{path}", "203.0.113.7"), ct);
        Assert.Equal(SimulationOutcomeKind.AcmeChallengeAnswer, simulated.Outcome!.Kind);
        Assert.Equal(200, simulated.Outcome.StatusCode);
        Assert.Equal(KeyAuthorization, simulated.Outcome.Body);

        // Escaped dots, and a path that isn't UTF-8.
        string escaped = await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes($"GET /a/%2E%2E/.well-known/acme-challenge/{Token} HTTP/1.1\r\nHost: nobody.test\r\nConnection: close\r\n\r\n"));
        Assert.Equal(200, Status(escaped));
        Assert.Equal(SimulationOutcomeKind.AcmeChallengeAnswer, AcmeKind(await SimulateAsync(host, new SimulationRequest($"http://nobody.test/a/%2E%2E/.well-known/acme-challenge/{Token}", "203.0.113.7"), ct)));

        int invalid = Status(await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET /%C3 HTTP/1.1\r\nHost: app.test\r\nConnection: close\r\n\r\n")));
        SimulationResult invalidSimulated = await SimulateAsync(host, new SimulationRequest("http://app.test/%C3", "203.0.113.7"), ct);
        Assert.Equal(200, invalid); // not a 400: the server leaves bytes that aren't UTF-8 encoded
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, invalidSimulated.Outcome!.Kind);
        Assert.Equal(200, invalidSimulated.Outcome.StatusCode);
    }

    private static SimulationOutcomeKind AcmeKind(SimulationResult result) => result.Outcome!.Kind;

    [Fact]
    public async Task ATls12ClientToANameThatRequiresTls13NeverGetsAHandshake()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = await StartAsync(ct);

        // Real: TLS 1.2 only is refused, TLS 1.3 works.
        Assert.False(await HandshakeSucceedsAsync(host, "strict.test", SslProtocols.Tls12, SslApplicationProtocol.Http11, ct));
        Assert.True(await HandshakeSucceedsAsync(host, "strict.test", SslProtocols.Tls13, SslApplicationProtocol.Http11, ct));
        Assert.True(await HandshakeSucceedsAsync(host, "app.test", SslProtocols.Tls12, SslApplicationProtocol.Http11, ct));

        SimulationResult tooOld = await SimulateAsync(host, new SimulationRequest("https://strict.test/", "203.0.113.7", Tls: new SimulationTls(TlsVersion: SimulationTlsVersion.Tls12)), ct);
        Assert.Equal(SimulationOutcomeKind.HandshakeRejected, tooOld.Outcome!.Kind);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, (await SimulateAsync(host, new SimulationRequest("https://strict.test/", "203.0.113.7"), ct)).Outcome!.Kind);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, (await SimulateAsync(host, new SimulationRequest("https://app.test/", "203.0.113.7", Tls: new SimulationTls(TlsVersion: SimulationTlsVersion.Tls12)), ct)).Outcome!.Kind);
    }

    [Fact]
    public async Task AnH2ClientToANameThatOffersOnlyHttp11FallsBackToHttp11()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = await StartAsync(ct);

        // Real: the same client negotiates h2 for app.test and http/1.1 for h1.test, and both are answered 200.
        using HttpClient client = EdgeClient.Create(out _);
        await AssertAnsweredOverAsync(host, client, "app.test", HttpVersion.Version20, ct);
        await AssertAnsweredOverAsync(host, client, "h1.test", HttpVersion.Version11, ct);

        SimulationResult app = await SimulateAsync(host, new SimulationRequest("https://app.test/", "203.0.113.7"), ct);
        SimulationResult h1 = await SimulateAsync(host, new SimulationRequest("https://h1.test/", "203.0.113.7"), ct);
        Assert.Equal("h2", Fact(app, SimulationStepKind.TlsHandshake, "alpn"));
        Assert.Equal("http/1.1", Fact(h1, SimulationStepKind.TlsHandshake, "alpn"));
        Assert.Equal(200, h1.Outcome!.StatusCode);

        // A connection negotiated for app.test (h2) that asks for h1.test is misdirected: a 421 on the wire, and in the trace.
        int status = await Http2Client.StatusAsync(host, "app.test", "h1.test", "/", [], ct);
        Assert.Equal(421, status);
        SimulationResult coalesced = await SimulateAsync(host, new SimulationRequest("https://h1.test/", "203.0.113.7", Tls: new SimulationTls("app.test")), ct);
        Assert.Equal(SimulationOutcomeKind.MisdirectedRequest, coalesced.Outcome!.Kind);
    }

    [Fact]
    public async Task OnHttp2TheAuthorityIsTheHostWhateverTheHostHeaderSays()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PaduraksaHost host = await StartAsync(ct);

        // Real: :authority names app.test (answered 200 by that host); a different Host header changes nothing.
        int same = await Http2Client.StatusAsync(host, "app.test", "app.test", "/", [], ct);
        int differs = await Http2Client.StatusAsync(host, "app.test", "app.test", "/", [("host", "nobody.test")], ct);
        int duplicate = await Http2Client.StatusAsync(host, "app.test", "app.test", "/", [("host", "nobody.test"), ("host", "other.test")], ct);

        Assert.Equal(200, same);
        SimulationRequest request = new("https://app.test/", "203.0.113.7", Headers: [new SimulationHeader("Host", "nobody.test")]);
        SimulationResult simulated = await SimulateAsync(host, request, ct);

        // What the server did with a Host header that disagrees is what the simulation must say; pinned below.
        Assert.Equal(differs, simulated.Outcome!.StatusCode ?? -1);
        Assert.Equal(differs, duplicate);
    }

    private static async Task AssertAnsweredOverAsync(PaduraksaHost host, HttpClient client, string name, Version expected, CancellationToken ct)
    {
        using HttpRequestMessage request = EdgeClient.Request(host, "https", name, "/", HttpVersion.Version20);
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(expected, response.Version);
    }

    private static async Task<bool> HandshakeSucceedsAsync(PaduraksaHost host, string serverName, SslProtocols protocols, SslApplicationProtocol alpn, CancellationToken ct)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Public, "https").Port, ct);
        try
        {
            await using SslStream tls = new(tcp.GetStream());
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = serverName,
                    EnabledSslProtocols = protocols,
                    ApplicationProtocols = [alpn],
                    RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
                },
                ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException)
        {
            return false;
        }
    }

    private static string Fact(SimulationResult result, SimulationStepKind kind, string name) =>
        Assert.Single(Assert.Single(result.Trace, s => s.Kind == kind).Facts, f => f.Name == name).Value;
}

/// <summary>A hand-written HTTP/2 client for one GET over TLS: just enough HPACK to say <c>:authority</c> and a <c>host</c> header
/// that disagree, and to read the status. Every other client normalises that away.</summary>
internal static class Http2Client
{
    public static async Task<int> StatusAsync(
        PaduraksaHost host, string serverName, string authority, string path, (string Name, string Value)[] headers, CancellationToken ct)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Public, "https").Port, ct);
        await using SslStream tls = new(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                ApplicationProtocols = [SslApplicationProtocol.Http2],
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
            },
            ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(SslApplicationProtocol.Http2, tls.NegotiatedApplicationProtocol);

        List<byte> block = [0x82, 0x87];
        Literal(block, 0x04, path);
        Literal(block, 0x01, authority);
        foreach ((string name, string value) in headers)
        {
            block.Add(0x00);
            Text(block, name);
            Text(block, value);
        }

        List<byte> request = [.. "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8];
        Frame(request, 0x4, 0x0, 0, []); // SETTINGS
        Frame(request, 0x1, 0x5, 1, [.. block]); // HEADERS, END_HEADERS | END_STREAM, stream 1
        await tls.WriteAsync(request.ToArray(), ct);
        await tls.FlushAsync(ct);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] header = new byte[9];
        while (true)
        {
            try
            {
                await tls.ReadExactlyAsync(header, timeout.Token);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException)
            {
                return -1;
            }

            int length = (header[0] << 16) | (header[1] << 8) | header[2];
            byte[] payload = new byte[length];
            await tls.ReadExactlyAsync(payload, timeout.Token);
            byte type = header[3];
            int stream = ((header[5] & 0x7f) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
            if (type == 0x4 && (header[4] & 0x1) == 0)
            {
                await tls.WriteAsync(new byte[] { 0, 0, 0, 4, 1, 0, 0, 0, 0 }, timeout.Token); // SETTINGS ack
            }
            else if (type == 0x1 && stream == 1)
            {
                return ParseStatus(payload);
            }
            else if (type is 0x3 or 0x7)
            {
                return -1; // RST_STREAM or GOAWAY: the server refused the request
            }
        }
    }

    private static int ParseStatus(byte[] block)
    {
        int first = block[0];
        if ((first & 0x80) != 0)
        {
            int[] statuses = [200, 204, 206, 304, 400, 404, 500];
            return statuses[(first & 0x7f) - 8];
        }

        // A literal with the :status name (index 8): the value follows as text.
        int length = block[1] & 0x7f;
        return int.Parse(Encoding.ASCII.GetString(block, 2, length), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Literal(List<byte> block, byte nameIndex, string value)
    {
        block.Add(nameIndex);
        Text(block, value);
    }

    private static void Text(List<byte> block, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        block.Add((byte)bytes.Length);
        block.AddRange(bytes);
    }

    private static void Frame(List<byte> to, byte type, byte flags, int stream, byte[] payload)
    {
        to.AddRange([(byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, type, flags]);
        to.AddRange([(byte)(stream >> 24), (byte)(stream >> 16), (byte)(stream >> 8), (byte)stream]);
        to.AddRange(payload);
    }
}
