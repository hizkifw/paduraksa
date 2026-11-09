using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// A real host with first-run setup done on the <b>normal</b> admin port and a config whose admin host is reached through the
/// <b>public</b> listener (PLAN §3.1). The public client pins the placeholder certificate the tests serve and sends the public
/// name as its <c>Host</c>; the helper exposes the proxied admin for the tests.
/// </summary>
internal sealed class AdminProxyHarness : IAsyncDisposable
{
    public const string AdminHost = "admin.test";
    public const string OtherHost = "other.test";
    public const string AdminPassword = AdminHarness.Password;

    private readonly AdminHarness _admin;

    private AdminProxyHarness(AdminHarness admin, HttpClient publicClient, string ownerCookie, string adminCookie)
    {
        _admin = admin;
        Public = publicClient;
        OwnerCookie = ownerCookie;
        AdminCookie = adminCookie;
    }

    public PaduraksaHost Host => _admin.Host;

    /// <summary>The public HTTPS client (pinned to the placeholder certificate).</summary>
    public HttpClient Public { get; }

    /// <summary>The Owner's session cookie from first-run setup on the admin port.</summary>
    public string OwnerCookie { get; }

    /// <summary>The Owner's session secret from signing in through the proxied admin (the cookie value only).</summary>
    public string AdminCookie { get; private set; }

    /// <summary>The <c>Cookie</c> header value for <see cref="AdminCookie"/>.</summary>
    public string SessionHeader => "__Host-paduraksa-session=" + AdminCookie;

    /// <summary>The admin host's ID in the committed config.</summary>
    public static HostId AdminHostId => TestIds.Host("admin-proxy");

    /// <summary>The other proxied host's ID (only when an upstream was given).</summary>
    public static HostId OtherHostId => TestIds.Host("other");

    public static async Task<AdminProxyHarness> StartAsync(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<HostRole, IServiceCollection>? configureServices = null,
        string? upstreamAddress = null,
        TrustedProxySettings? trustedProxies = null,
        LogCapture? logs = null,
        HostSettings? adminHostSettings = null)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AdminHarness admin = await AdminHarness.StartAsync(settings, configureServices, logs);
        string ownerCookie = await admin.SetUpOwnerAsync();

        List<HostConfig> hosts =
        [
            new HostConfig(TestIds.Host("admin-proxy"), [AdminHost], Kind: HostKind.Admin, Settings: adminHostSettings ?? new HostSettings()),
        ];
        List<UpstreamConfig> upstreams = [];
        if (upstreamAddress is not null)
        {
            upstreams.Add(ConfigTestSupport.Upstream("echo", upstreamAddress));
            hosts.Add(new HostConfig(
                TestIds.Host("other"),
                [OtherHost],
                Routes: [new RouteConfig(TestIds.Route("other-root"), TestIds.Upstream("echo"))]));
        }

        ConfigDocument document = new(
            Settings: new GlobalSettings(TrustedProxies: trustedProxies),
            Upstreams: upstreams,
            Hosts: hosts);
        CurrentConfig current = await admin.Host.Shared.Config.GetCurrentAsync(cancellationToken);
        await ConfigTestSupport.CommitAsync(admin.Host, new ReplaceConfigCommand(document, current.Revision?.Id), cancellationToken);

        HttpClient publicClient = TestHost.CreateClient(admin.Host);
        publicClient.Timeout = TimeSpan.FromSeconds(20);
        AdminProxyHarness harness = new(admin, publicClient, ownerCookie, ownerCookie);

        // Sign in once through the proxy, so the proxied session cookie is a real one.
        using HttpResponseMessage login = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", Body.Login(AdminHarness.OwnerName, AdminPassword),
            origin: "https://" + AdminHost);
        if (login.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"The proxied sign-in was refused: {login.StatusCode} {await login.Content.ReadAsStringAsync(cancellationToken)}");
        }
        harness.AdminCookie = SessionCookieOf(login) ?? throw new InvalidOperationException("The proxied sign-in set no session cookie.");
        return harness;
    }

    /// <summary>A request to the public listener for <paramref name="host"/> (the admin host by default).</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, string host = AdminHost)
    {
        HttpRequestMessage request = new(method, new Uri(TestHost.Endpoint(Host, HostRole.Public, "https"), path));
        request.Headers.Host = host;
        return request;
    }

    /// <summary>
    /// Sends a request through the public listener. <paramref name="origin"/> sets <c>Origin</c>; <paramref name="fetchSite"/>
    /// sets <c>Sec-Fetch-Site</c>; <paramref name="cookie"/> is a raw <c>Cookie</c> header value.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? cookie = null,
        string host = AdminHost,
        string? origin = null,
        string? fetchSite = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using HttpRequestMessage request = Request(method, path, host);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (fetchSite is not null)
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        }

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await Public.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static string? SessionCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.Select(line => line.Split(';', 2)[0])
                .Where(pair => pair.StartsWith("__Host-paduraksa-session=", StringComparison.Ordinal) && pair.Length > "__Host-paduraksa-session=".Length)
                .Select(pair => pair["__Host-paduraksa-session=".Length..])
                .LastOrDefault()
            : null;

    public AdminHarness Admin => _admin;

    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(string action) => await _admin.AuditAsync(action);

    public async ValueTask DisposeAsync()
    {
        Public.Dispose();
        await _admin.DisposeAsync();
    }
}
