using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Admin.Auth;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>An admin response with its body read, and the session cookie it set (if any).</summary>
internal sealed class AdminResponse(HttpResponseMessage message, string body) : IDisposable
{
    public HttpStatusCode Status => message.StatusCode;

    public HttpResponseHeaders Headers => message.Headers;

    public HttpContentHeaders ContentHeaders => message.Content.Headers;

    public string Body => body;

    public JsonElement Json => JsonDocument.Parse(body).RootElement;

    /// <summary>The raw <c>Set-Cookie</c> lines.</summary>
    public IReadOnlyList<string> SetCookies => message.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? [.. values] : [];

    /// <summary>The value the session cookie was set to, or null (also null when it was cleared).</summary>
    public string? SessionCookie
    {
        get
        {
            string? line = SetCookies.LastOrDefault(c => c.StartsWith(AdminSessionCookie.Name + "=", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            string value = line[(AdminSessionCookie.Name.Length + 1)..].Split(';')[0];
            return value.Length == 0 ? null : value;
        }
    }

    public string? Header(string name) => message.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;

    public void Dispose() => message.Dispose();
}

/// <summary>A real host (ephemeral ports, loopback admin over HTTPS) and a client that handles cookies by hand, as a script would.</summary>
internal sealed class AdminHarness : IAsyncDisposable
{
    public const string OwnerName = "owner";
    public const string Password = "correct horse battery staple";

    private readonly HttpClient _http;

    private AdminHarness(PaduraksaHost host, HttpClient http, Uri admin)
    {
        Host = host;
        _http = http;
        Admin = admin;
    }

    public PaduraksaHost Host { get; }

    public Uri Admin { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<AdminHarness> StartAsync(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<HostRole, IServiceCollection>? configureServices = null,
        LogCapture? logs = null)
    {
        Dictionary<string, string?> config = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Admin:PasswordIterations"] = "100000", // the validator's floor: sign-in tests do dozens of derivations
        };
        foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        PaduraksaHost host = TestHost.Create(settings: config, configureServices: configureServices, logs: logs);
        try
        {
            await host.StartAsync(Ct);
        }
        catch
        {
            // A test that retries after a lost port race must not leak the half-started host (its vault, database and hosts).
            await host.DisposeAsync();
            throw;
        }

        // The hardening tests reach the admin by a LAN name or a made-up one over a loopback connection, so the client
        // must not insist the certificate covers that name (it is still pinned or chain-checked to the local CA).
        HttpClient http = TestHost.CreateAdminClient(host, static handler =>
        {
            handler.UseCookies = false;
            handler.AllowAutoRedirect = false;
        }, checkServerName: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        return new AdminHarness(host, http, TestHost.Endpoint(host, HostRole.Admin, "https"));
    }

    /// <summary>
    /// Sends a request. A browser's headers are the default (<c>Sec-Fetch-Site: same-origin</c>); pass <paramref name="browser"/> false to
    /// send none, as curl does. <paramref name="json"/> is the raw body.
    /// </summary>
    public async Task<AdminResponse> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? cookie = null,
        string? bearer = null,
        bool browser = true,
        IReadOnlyDictionary<string, string>? headers = null,
        string contentType = "application/json",
        Version? version = null,
        bool streamed = false)
    {
        using HttpRequestMessage request = new(method, new Uri(Admin, path));
        if (version is not null)
        {
            request.Version = version;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        if (json is not null)
        {
            // Streamed: a body of unknown length (chunked on HTTP/1.1, no Content-Length on HTTP/2).
            request.Content = streamed
                ? new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(json)))
                : new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{AdminSessionCookie.Name}={cookie}");
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (browser)
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        }

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response = await _http.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);
        return new AdminResponse(response, body);
    }

    public Task<AdminResponse> GetAsync(string path, string? cookie = null, string? bearer = null) =>
        SendAsync(HttpMethod.Get, path, cookie: cookie, bearer: bearer);

    public Task<AdminResponse> PostAsync(string path, string? json = null, string? cookie = null, string? bearer = null) =>
        SendAsync(HttpMethod.Post, path, json ?? "{}", cookie, bearer);

    public async Task<string> SetupTokenAsync() =>
        await Host.Shared.Setup.EnsureTokenAsync(Ct) ?? throw new InvalidOperationException("Setup is already complete.");

    /// <summary>Completes first-run setup through the API and returns the Owner's session cookie.</summary>
    public async Task<string> SetUpOwnerAsync()
    {
        string token = await SetupTokenAsync();
        using AdminResponse response = await PostAsync("/api/v1/setup", Body.Setup(token, OwnerName, Password));
        Assert.Equal(HttpStatusCode.Created, response.Status);
        return response.SessionCookie ?? throw new InvalidOperationException("Setup set no session cookie.");
    }

    /// <summary>Signs in through the API (no second factor) and returns the cookie.</summary>
    public async Task<string> SignInAsync(string username, string password = Password)
    {
        using AdminResponse response = await PostAsync("/api/v1/auth/login", Body.Login(username, password));
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.False(response.Json.GetProperty("secondFactorRequired").GetBoolean());
        return response.SessionCookie!;
    }

    /// <summary>Creates a user as the Owner (through the API) with <paramref name="role"/>, and returns their ID.</summary>
    public async Task<string> CreateUserAsync(string ownerCookie, string username, string role)
    {
        using AdminResponse response = await PostAsync("/api/v1/users", $$"""{"username":"{{username}}","displayName":null,"password":"{{Password}}","roles":["{{role}}"]}""", ownerCookie);
        Assert.Equal(HttpStatusCode.Created, response.Status);
        return response.Json.GetProperty("id").GetString()!;
    }

    /// <summary>Issues a token through the API as <paramref name="cookie"/>'s user and returns (id, secret).</summary>
    public async Task<(string Id, string Secret)> IssueTokenAsync(string cookie, params string[] scopes)
    {
        using AdminResponse response = await PostAsync("/api/v1/tokens", $$"""{"name":"test","scopes":[{{string.Join(",", scopes.Select(s => $"\"{s}\""))}}],"expiresAt":null}""", cookie);
        Assert.Equal(HttpStatusCode.Created, response.Status);
        return (response.Json.GetProperty("summary").GetProperty("id").GetString()!, response.Json.GetProperty("token").GetString()!);
    }

    /// <summary>The audit entries of one action, oldest first, read straight from the config database.</summary>
    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(string action)
    {
        IDbContextFactory<ConfigDbContext> contexts = Host.GetServices(HostRole.Admin).GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using ConfigDbContext context = await contexts.CreateDbContextAsync(Ct);
        return await context.Database
            .SqlQueryRaw<AuditRecord>("""SELECT "Action", "Outcome", "Channel", "ClientIp", "Target", "Details" FROM "AuditEvents" WHERE "Action" = {0} ORDER BY "Timestamp", "rowid" """, action)
            .ToListAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await Host.DisposeAsync();
    }
}

/// <summary>An audit entry as the tests need it.</summary>
internal sealed class AuditRecord
{
    public string Action { get; set; } = "";

    public string Outcome { get; set; } = "";

    public string Channel { get; set; } = "";

    public string? ClientIp { get; set; }

    public string? Target { get; set; }

    public string? Details { get; set; }
}

/// <summary>Request bodies, as raw JSON.</summary>
internal static class Body
{
    public static string Login(string username, string password) => $$"""{"username":"{{username}}","password":"{{password}}"}""";

    public static string Setup(string token, string username, string password) =>
        $$"""{"setupToken":"{{token}}","username":"{{username}}","password":"{{password}}","displayName":null}""";

    public static string Password(string password) => $$"""{"password":"{{password}}"}""";
}

/// <summary>A readable stream that reports no length, so the HTTP client cannot send a Content-Length.</summary>
internal sealed class NonSeekableStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
