using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Json;
using Paduraksa.Admin.Mcp;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// A real host with the API on its admin listener (HTTPS, pinned certificate) and real authentication: the first Owner comes from first-run setup,
/// a user in every other role is created through the identity services, and each of them holds an API token (scoped to what their role grants) that a
/// request presents as a Bearer token. A role name is the "caller" of a request; <c>null</c> is anonymous. <see cref="SignInAsync"/> makes a real
/// session instead, for the tests of what only a cookie can do (CSRF).
/// </summary>
internal sealed class AdminApiHarness : IAsyncDisposable
{
    public const string Password = "correct horse battery staple";
    public const string SessionCookieName = "__Host-paduraksa-session";

    private static readonly Role[] Roles = [Role.Admin, Role.Operator, Role.Viewer, Role.Auditor];

    private readonly Dictionary<string, UserId> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiTokenId> _tokenIds = new(StringComparer.Ordinal);

    private AdminApiHarness(PaduraksaHost host, HttpClient client, Uri baseAddress)
    {
        Host = host;
        Client = client;
        BaseAddress = baseAddress;
    }

    public PaduraksaHost Host { get; }

    public HttpClient Client { get; }

    public Uri BaseAddress { get; }

    /// <summary>Starts a host. <paramref name="adminServices"/> may replace services of the admin app (a test that needs a failing dependency).</summary>
    public static async Task<AdminApiHarness> StartAsync(CancellationToken ct, LogCapture? logs = null, Action<IServiceCollection>? adminServices = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var host = TestHost.Create(
            settings: settings,
            logs: logs,
            configureShared: TestHost.NoAccessLogFile,
            configureServices: (role, services) =>
            {
                if (role == HostRole.Admin)
                {
                    // The test process is not the server binary: the sandbox children are started from the server project's own output.
                    services.AddSingleton(new AdminMcpOptions(Paduraksa.Tests.Integration.Mcp.SandboxFixture.Command, TimeSpan.FromMinutes(10)));
                    adminServices?.Invoke(services);
                }
            });
        await host.StartAsync(ct);
        // The admin listener serves the process's local-CA certificate, so the harness trusts that root. It sends
        // cookies by hand (the CSRF tests set them explicitly), so the handler must not keep a jar of its own.
        var client = TestHost.CreateAdminClient(host, static handler =>
        {
            handler.UseCookies = false;
            // For a request that holds its body back until the server has answered (SendAsync's expectContinue): the default wait of 1 s ends on a
            // loaded runner before the answer, and the body then goes out anyway.
            handler.Expect100ContinueTimeout = TimeSpan.FromSeconds(30);
        });
        AdminApiHarness harness = new(host, client, TestHost.Endpoint(host, HostRole.Admin, "https"));
        await harness.CreateUsersAsync(ct);
        return harness;
    }

    private async Task CreateUsersAsync(CancellationToken ct)
    {
        ClientContext client = new(Channel.Api, IPAddress.Loopback, "test");
        var token = (await File.ReadAllTextAsync(Path.Combine(Host.Options.DataDirectory, SetupService.TokenFileName), ct)).Trim();
        var owner = (await Host.Shared.Setup.CompleteSetupAsync(token, "root", Password, null, client, ct)).Owner!;
        await IssueTokenAsync("owner", owner.Id, RolePermissions.All, ct);
        foreach (var role in Roles)
        {
            var name = role.ToName();
            var user = (await Host.Shared.Users.CreateAsync(CommandOrigin.Cli, new CreateUserRequest(name, null, Password, [role]), ct)).User!;
            await IssueTokenAsync(name, user.Id, RolePermissions.Of(role), ct);
        }
    }

    private async Task IssueTokenAsync(string name, UserId user, IEnumerable<Permission> scopes, CancellationToken ct)
    {
        _users[name] = user;
        var issued = await Host.Shared.ApiTokens.IssueAsync(
            new CommandOrigin(Actor.ForUser(user), Channel.Api, IPAddress.Loopback),
            new IssueApiTokenRequest($"test {name}", [.. scopes]),
            ct);
        _tokenIds[name] = issued.Issued!.Summary.Id;
        _tokens[name] = issued.Issued?.Token ?? throw new InvalidOperationException($"No token for {name}: {issued.Status} {issued.Problem}");
    }

    /// <summary>The ID of the API token a role's test user presents (the actor of what that user does through the API).</summary>
    public ApiTokenId TokenOf(string role) => _tokenIds[role];

    /// <summary>The API token a role's test user presents (what a script or the CLI would be given).</summary>
    public string TokenValueOf(string role) => _tokens[role];

    /// <summary>The user id of a role's test user.</summary>
    public UserId UserOf(string role) => _users[role];

    /// <summary>Signs a role's user in through the real login endpoint and returns the session cookie as a <c>Cookie</c> header value.</summary>
    public async Task<string> SignInAsync(string role)
    {
        using HttpRequestMessage login = new(HttpMethod.Post, new Uri(BaseAddress, "/api/v1/auth/login"));
        login.Headers.Add("Sec-Fetch-Site", "same-origin");
        login.Content = JsonBody($$"""{"username":"{{(role == "owner" ? "root" : role)}}","password":"{{Password}}"}""");
        using var response = await Client.SendAsync(login, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SessionCookieName + "=", StringComparison.Ordinal));
        return setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>A request to the API, as <paramref name="caller"/> (a role name, presenting its token; null is anonymous).</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, string? caller = "owner")
    {
        HttpRequestMessage request = new(method, new Uri(BaseAddress, path));
        if (caller is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _tokens[caller]);
        }

        return request;
    }

    /// <summary>
    /// A request to the API as <paramref name="caller"/>. <paramref name="expectContinue"/> holds the body back until the server has answered: needed
    /// for a body the server refuses by its declared length, as it answers and closes and a client still writing sees a reset (a broken pipe) instead.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? caller = "owner", string? ifMatch = null, HttpContent? content = null, bool expectContinue = false)
    {
        using var request = Request(method, path, caller);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (expectContinue)
        {
            request.Headers.ExpectContinue = true;
        }

        request.Content = content;
        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A request with a session cookie and the given browser headers (<c>Sec-Fetch-Site</c>, <c>Origin</c>), or neither.</summary>
    public async Task<HttpResponseMessage> SendWithCookieAsync(HttpMethod method, string path, string cookie, string? fetchSite = null, string? origin = null, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, new Uri(BaseAddress, path));
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (fetchSite is not null)
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        request.Content = content;
        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? caller = "owner") =>
        SendAsync(HttpMethod.Get, path, caller);

    public Task<HttpResponseMessage> SendJsonAsync<T>(HttpMethod method, string path, T body, JsonTypeInfo<T> typeInfo, string? caller = "owner", string? ifMatch = null) =>
        SendAsync(method, path, caller, ifMatch, JsonBody(JsonSerializer.Serialize(body, typeInfo)));

    public static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>The problem a response carries; fails the test when the body isn't one.</summary>
    public static async Task<ApiProblem> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(text, AdminJsonContext.Default.ApiProblem) ?? throw new InvalidOperationException("Not a problem: " + text);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize(text, typeInfo) ?? throw new InvalidOperationException("Empty body.");
    }

    public static string? ETag(HttpResponseMessage response) => response.Headers.ETag?.Tag;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync(CancellationToken.None);
        await Host.DisposeAsync();
    }
}
