using System.Net;
using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Tests.Vault.Secrets;

/// <summary>
/// The authenticating handler (<see cref="ISecretHttpHandlerFactory"/>) over a recording transport: what goes on the wire,
/// what the caller can see, and what is refused before anything is sent. The real socket handler is covered in
/// <see cref="AuthenticatingHandlerWireTests"/>.
/// </summary>
public sealed class AuthenticatingHandlerTests : IDisposable
{
    private const string Token = "cf-token-s3cr3t-VALUE";

    private readonly VaultHarness _vault = new();

    private readonly RecordingHandler _wire = new();

    public AuthenticatingHandlerTests() => _vault.Terminal = _ => _wire;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISecretStore Store => _vault.Get<ISecretStore>();

    public void Dispose() => _vault.Dispose();

    private async Task<SecretId> PutToken(params string[] hosts) =>
        await Store.PutAsync(SecretKind.DnsApiToken, Encoding.UTF8.GetBytes(Token), SecretStoreTests.Dns(hosts), Ct);

    private HttpClient Client(SecretId id, SecretInjection how) =>
        new(_vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(id, how, SecretHttpTransportOptions.Default));

    [Fact]
    public async Task BearerIsInjectedOnTheWireButNotVisibleToTheCaller()
    {
        var id = await PutToken("api.example.com");
        using var client = Client(id, SecretInjection.Bearer(["api.example.com"]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/zones");

        using var response = await client.SendAsync(request, Ct);

        var seen = Assert.Single(_wire.Requests);
        Assert.Equal($"Bearer {Token}", seen.Headers["Authorization"]);
        Assert.Equal("https://api.example.com/zones", seen.Uri.ToString());
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Same(request, response.RequestMessage);
        Assert.False(response.RequestMessage!.Headers.Contains("Authorization"));
        Assert.NotSame(request, seen.Request);
    }

    [Fact]
    public async Task OtherInjectionKindsPutTheCredentialWhereTheyShould()
    {
        var id = await PutToken("api.example.com");
        var hosts = new[] { "api.example.com" };

        using (var client = Client(id, SecretInjection.Authorization("Token", hosts)))
        {
            await client.GetAsync("https://api.example.com/a", Ct);
        }

        using (var client = Client(id, SecretInjection.Header("X-Auth-Token", hosts)))
        {
            await client.GetAsync("https://api.example.com/b", Ct);
        }

        using (var client = Client(id, SecretInjection.BasicAuth("user", hosts)))
        {
            await client.GetAsync("https://api.example.com/c", Ct);
        }

        Assert.Equal($"Token {Token}", _wire.Requests[0].Headers["Authorization"]);
        Assert.Equal(Token, _wire.Requests[1].Headers["X-Auth-Token"]);
        Assert.False(_wire.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"user:{Token}")), _wire.Requests[2].Headers["Authorization"]);
    }

    [Fact]
    public async Task ACredentialHeaderTheCallerSetIsOverwritten()
    {
        var id = await PutToken("api.example.com");
        using var client = Client(id, SecretInjection.Bearer(["api.example.com"]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer attacker-chosen");

        await client.SendAsync(request, Ct);

        Assert.Equal($"Bearer {Token}", Assert.Single(_wire.Requests).Headers["Authorization"]);
        Assert.Equal("Bearer attacker-chosen", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task BodyAndHeadersArriveOptionsStayBehindAndTheRequestCanBeSentAgain()
    {
        var id = await PutToken("api.example.com");
        // An invoker, not an HttpClient: the client itself refuses to send one request object twice.
        using var client = new HttpMessageInvoker(_vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(
            id, SecretInjection.Bearer(["api.example.com"]), SecretHttpTransportOptions.Default));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/records")
        {
            Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version11,
        };
        request.Headers.Add("X-Trace", "t1");
        request.Options.Set(new HttpRequestOptionsKey<string>("k"), "v");

        await client.SendAsync(request, Ct);
        await client.SendAsync(request, Ct);

        Assert.Equal(2, _wire.Requests.Count);
        Assert.All(_wire.Requests, r =>
        {
            Assert.Equal("{\"a\":1}", r.Body);
            Assert.Equal("t1", r.Headers["X-Trace"]);
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.False(r.Request.Options.TryGetValue(new HttpRequestOptionsKey<string>("k"), out _));
        });

        // The clone's disposal did not take the caller's content with it.
        Assert.Equal("{\"a\":1}", await request.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PlainHttpAndRelativeUrisAreRefusedBeforeAnythingIsSent()
    {
        var id = await PutToken("api.example.com");
        using var client = Client(id, SecretInjection.Bearer(["api.example.com"]));

        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("http://api.example.com/x", Ct));
        using var relative = new HttpRequestMessage(HttpMethod.Get, new Uri("/x", UriKind.Relative));
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await new HttpMessageInvoker(
            _vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(id, SecretInjection.Bearer(["api.example.com"]), SecretHttpTransportOptions.Default)).SendAsync(relative, Ct));

        Assert.Empty(_wire.Requests);
    }

    [Fact]
    public async Task AHostOutsideTheCallersListOrTheSecretsListIsRefused()
    {
        var id = await PutToken("b.example.com", "c.example.com");
        using var client = Client(id, SecretInjection.Bearer(["a.example.com", "b.example.com"]));

        // a: the caller allows it, the secret does not. c: the secret allows it, the caller does not. d: neither.
        foreach (var host in new[] { "a", "c", "d" })
        {
            var ex = await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync($"https://{host}.example.com/x", Ct));
            Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        }

        Assert.Empty(_wire.Requests);
        await client.GetAsync("https://b.example.com/x", Ct);
        Assert.Single(_wire.Requests);
    }

    [Fact]
    public async Task HostsAreComparedNormalisedAndByTheRealHostNotTheUserInfo()
    {
        var id = await PutToken("api.example.com", "xn--bcher-kva.example");
        using var client = Client(id, SecretInjection.Bearer(["API.example.com", "bücher.example"]));

        await client.GetAsync("https://API.Example.COM/x", Ct);
        await client.GetAsync("https://api.example.com:443/x", Ct);
        await client.GetAsync("https://api.example.com./x", Ct);
        await client.GetAsync("https://BÜCHER.example/x", Ct);
        Assert.Equal(4, _wire.Requests.Count);

        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://api.example.com@evil.example/x", Ct));
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://api.example.com.evil.example/x", Ct));
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://[::1]/x", Ct));
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://127.0.0.1/x", Ct));
        Assert.Equal(4, _wire.Requests.Count);
    }

    [Fact]
    public async Task OnlyPort443IsAllowedUnlessAnEntryNamesAnotherPort()
    {
        var id = await PutToken("api.example.com", "api.example.com:8443", "only-secret.example:9000");
        using var client = Client(id, SecretInjection.Bearer(["api.example.com", "API.example.com:8443", "api.example.com:9443", "only-secret.example"]));

        await client.GetAsync("https://api.example.com/a", Ct);
        await client.GetAsync("https://api.example.com:443/b", Ct);
        await client.GetAsync("https://api.example.com:8443/c", Ct);
        Assert.Equal(3, _wire.Requests.Count);

        // Another port: the caller names it but the secret does not; or neither does; or only the secret does.
        foreach (var url in new[] { "https://api.example.com:9443/x", "https://api.example.com:8444/x", "https://only-secret.example:9000/x", "https://only-secret.example/x" })
        {
            await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync(url, Ct));
        }

        Assert.Equal(3, _wire.Requests.Count);

        // An entry for 443 is the same as none.
        Assert.Equal(["api.example.com"], (await Store.DescribeAsync(
            await Store.PutAsync(SecretKind.DnsApiToken, "x"u8.ToArray(), SecretStoreTests.Dns("api.example.com:443"), Ct), Ct))!.AllowedHosts);
    }

    [Fact]
    public async Task AHostHeaderThatNamesAnotherDestinationIsRefusedAndNeverCopied()
    {
        var id = await PutToken("api.example.com", "evil.example");
        using var invoker = new HttpMessageInvoker(_vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(
            id, SecretInjection.Bearer(["api.example.com", "evil.example"]), SecretHttpTransportOptions.Default));

        // Domain fronting: allowed URL, other Host. Even when the other host is itself allowed, the two must agree.
        foreach (var header in new[] { "attacker.example", "evil.example", "api.example.com:8443" })
        {
            using var fronted = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");
            fronted.Headers.Host = header;
            await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await invoker.SendAsync(fronted, Ct));
        }

        Assert.Empty(_wire.Requests);

        // The same destination spelled differently is fine, and is not forwarded as a header: the clone's comes from its URL.
        using var same = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");
        same.Headers.Host = "API.example.com.:443";
        await invoker.SendAsync(same, Ct);
        Assert.False(Assert.Single(_wire.Requests).Headers.ContainsKey("Host"));
    }

    [Fact]
    public async Task ASecretOfAnotherKindOrNoSecretIsRefused()
    {
        var tsig = await Store.PutAsync(SecretKind.TsigKey, Encoding.UTF8.GetBytes(Token), SecretStoreTests.Mac(), Ct);
        var totp = await Store.PutAsync(SecretKind.TotpSecret, Encoding.UTF8.GetBytes(Token), new(null, null, null), Ct);

        foreach (var id in new[] { tsig, totp, SecretId.New() })
        {
            using var client = Client(id, SecretInjection.Bearer(["api.example.com"]));
            await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://api.example.com/x", Ct));
        }

        Assert.Empty(_wire.Requests);
    }

    [Fact]
    public async Task RotationDeletionAndANewAllowlistTakeEffectOnTheNextRequest()
    {
        var id = await PutToken("api.example.com");
        using var client = Client(id, SecretInjection.Bearer(["api.example.com", "other.example.com"]));
        await client.GetAsync("https://api.example.com/1", Ct);

        await Store.ReplaceAsync(id, "rotated-token"u8.ToArray(), null, Ct);
        await client.GetAsync("https://api.example.com/2", Ct);

        await Store.ReplaceAsync(id, "rotated-token"u8.ToArray(), SecretStoreTests.Dns("other.example.com"), Ct);
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://api.example.com/3", Ct));
        await client.GetAsync("https://other.example.com/4", Ct);

        await Store.DeleteAsync(id, Ct);
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://other.example.com/5", Ct));

        Assert.Equal(
            ["Bearer " + Token, "Bearer rotated-token", "Bearer rotated-token"],
            _wire.Requests.Select(r => r.Headers["Authorization"]));
    }

    [Fact]
    public async Task ASecretThatCannotBeAHeaderValueIsRefusedWithoutEchoingIt()
    {
        var injected = await Store.PutAsync(SecretKind.DnsApiToken, "tok\r\nX-Evil: 1"u8.ToArray(), SecretStoreTests.Dns("api.example.com"), Ct);
        var binary = await Store.PutAsync(SecretKind.DnsApiToken, new byte[] { 0xFF, 0xFE, 0x41 }, SecretStoreTests.Dns("api.example.com"), Ct);

        foreach (var id in new[] { injected, binary })
        {
            using var client = Client(id, SecretInjection.Bearer(["api.example.com"]));
            var ex = await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync("https://api.example.com/x", Ct));
            Assert.DoesNotContain("X-Evil", ex.Message, StringComparison.Ordinal);
        }

        Assert.Empty(_wire.Requests);
    }

    [Fact]
    public async Task ACallerCannotReachTheTransportThroughTheReturnedHandler()
    {
        var id = await PutToken("api.example.com");

        var handler = _vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(id, SecretInjection.Bearer(["api.example.com"]), SecretHttpTransportOptions.Default);

        Assert.IsType<AuthenticatingHandler>(handler);
        Assert.False(handler is DelegatingHandler);
        Assert.DoesNotContain(
            typeof(AuthenticatingHandler).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
            p => typeof(HttpMessageHandler).IsAssignableFrom(p.PropertyType) || typeof(HttpMessageInvoker).IsAssignableFrom(p.PropertyType));
        handler.Dispose();
        Assert.True(_wire.Disposed);
    }

    [Fact]
    public async Task SynchronousSendIsNotSupported()
    {
        var id = await PutToken("api.example.com");
        using var client = Client(id, SecretInjection.Bearer(["api.example.com"]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/x");

        Assert.Throws<NotSupportedException>(() => client.Send(request, Ct));
    }

    [Fact]
    public async Task CreatingAHandlerDoesNoIoAndNeedsNoSecret()
    {
        _ = _vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(
            SecretId.New(), SecretInjection.Bearer(["api.example.com"]), SecretHttpTransportOptions.Default);

        Assert.False(File.Exists(_vault.Data.File("vault.db")));
        await Task.CompletedTask;
    }

    [Fact]
    public void TheDefaultTransportIsALockedDownSocketsHandler()
    {
        using var handler = TerminalHandlers.CreateSockets(new(
            TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), new Uri("http://proxy.example:3128")));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.PooledConnectionIdleTimeout);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Null(handler.Credentials);

        using var direct = TerminalHandlers.CreateSockets(SecretHttpTransportOptions.Default);
        Assert.False(direct.UseProxy);
        Assert.Null(direct.Proxy);
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalHandlers.CreateSockets(SecretHttpTransportOptions.Default with { ConnectTimeout = TimeSpan.Zero }));
    }
}

/// <summary>Records what reaches the end of the chain, and answers 200 (or what <see cref="Respond"/> says).</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    public bool Disposed { get; private set; }

    public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SeenRequest(request, request.Method, request.RequestUri!, headers, body));
        return Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed record SeenRequest(HttpRequestMessage Request, HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);
