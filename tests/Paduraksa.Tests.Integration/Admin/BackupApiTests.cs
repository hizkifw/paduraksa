using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Hosting;
using static Paduraksa.Tests.Integration.Admin.AdminApiHarness;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// <c>POST /api/v1/backup</c> over the admin port: the Owner's alone, a token scope, the archive as a download, and the passphrase only ever
/// in the request body (never a log line, an audit event or an answer).
/// </summary>
public sealed class BackupApiTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";

    private readonly LogCapture _logs = new();

    private AdminApiHarness _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _api = await StartAsync(Ct, _logs);

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static StringContent Body(string passphrase = Passphrase) => JsonBody($$"""{"passphrase":"{{passphrase}}"}""");

    private Task<HttpResponseMessage> PostAsync(string? caller, string passphrase = Passphrase) =>
        _api.SendAsync(HttpMethod.Post, "/api/v1/backup", caller, content: Body(passphrase));

    [Fact]
    public async Task TheOwnerGetsTheArchiveAsADownloadThatIsNeverCached()
    {
        using var response = await PostAsync("owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^paduraksa-backup-\d{8}-\d{6}\.pdkbak$", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("PDKBAK", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.DoesNotContain(Passphrase, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_api.Host.Options.DataDirectory, "run"), "download-*"));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("viewer")]
    [InlineData("auditor")]
    public async Task EveryOtherRoleIsForbidden(string role)
    {
        using var response = await PostAsync(role);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.permissionRequired", (await ProblemAsync(response, Ct)).Code);
    }

    [Fact]
    public async Task AnAnonymousCallerIsUnauthorized()
    {
        using var response = await PostAsync(caller: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenWithTheScopeMayAndOneWithoutItMayNotEvenForTheOwner()
    {
        var scoped = await IssueAsync("owner", Permission.BackupCreate);
        var other = await IssueAsync("owner", Permission.ConfigRead);

        using var allowed = await BearerAsync(scoped);
        using var refused = await BearerAsync(other);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task ATokenOfALesserUserCannotBeIssuedTheScopeAtAll()
    {
        var issued = await _api.Host.Shared.ApiTokens.IssueAsync(
            new CommandOrigin(Actor.ForUser(_api.UserOf("admin")), Channel.Api, IPAddress.Loopback), new IssueApiTokenRequest("backup", [Permission.BackupCreate]), Ct);

        Assert.False(issued.Succeeded);
        Assert.Null(issued.Issued);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eleven char")]
    public async Task AWeakPassphraseIsA422WithItsOwnCode(string passphrase)
    {
        using var response = await PostAsync("owner", passphrase);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response, Ct);
        Assert.Equal("backup.passphrase", problem.Code);
        Assert.DoesNotContain(passphrase.Length == 0 ? "\u0000" : passphrase, problem.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatIsNotTheRequestIsRefusedBeforeAnything()
    {
        using var noBody = await _api.SendAsync(HttpMethod.Post, "/api/v1/backup", "owner", content: JsonBody(string.Empty));
        using var wrongShape = await _api.SendAsync(HttpMethod.Post, "/api/v1/backup", "owner", content: JsonBody("""{"password":"correct horse battery staple"}"""));
        using var notJson = await _api.SendAsync(HttpMethod.Post, "/api/v1/backup", "owner", content: new StringContent("passphrase=correct horse battery staple", Encoding.UTF8, "application/x-www-form-urlencoded"));
        using var get = await _api.GetAsync("/api/v1/backup");
        using var noContentType = await _api.SendAsync(HttpMethod.Post, "/api/v1/backup", "owner");

        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongShape.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode); // there is no GET: a download is only ever made by a POST that carries the passphrase
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noContentType.StatusCode);
    }

    [Fact]
    public async Task OnlyOneBackupRunsAtATimeAndTheOtherIsToldSo()
    {
        var first = PostAsync("owner");
        var second = PostAsync("owner");
        using var a = await first;
        using var b = await second;

        var statuses = new[] { a.StatusCode, b.StatusCode }.Order().ToList();

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
        var busy = a.StatusCode == HttpStatusCode.Conflict ? a : b;
        Assert.Equal("backup.busy", (await ProblemAsync(busy, Ct)).Code);
    }

    [Fact]
    public async Task TheBackupIsAuditedWithWhoAndHowBigAndNeitherTheAuditLogNorAnyLogHoldsThePassphrase()
    {
        using var response = await PostAsync("owner");
        var size = (await response.Content.ReadAsByteArrayAsync(Ct)).Length;
        using var denied = await PostAsync("viewer");

        using var audit = await _api.GetAsync("/api/v1/audit?action=backup.create");
        var text = await audit.Content.ReadAsStringAsync(Ct);
        var items = JsonNode.Parse(text)!["items"]!.AsArray();

        // The refused viewer never reaches the service (the endpoint's permission filter answers), so only the Owner's is recorded.
        var success = Assert.Single(items)!;
        Assert.Equal("success", success["outcome"]!.GetValue<string>());
        Assert.Equal("token", success["actor"]!["kind"]!.GetValue<string>());
        Assert.Equal(size, success["details"]!["bytes"]!.GetValue<long>());
        Assert.DoesNotContain(Passphrase, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(Passphrase, StringComparison.Ordinal) || (e.Exception?.ToString().Contains(Passphrase, StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task TheOpenApiDocumentDescribesItWithItsPermissionAndNoRestoreEndpointExists()
    {
        using var response = await _api.GetAsync("/api/v1/openapi.json", caller: null);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        var operation = document["paths"]!["/api/v1/backup"]!["post"]!;

        Assert.Equal("backup:create", operation["x-required-permission"]!.GetValue<string>());
        Assert.NotNull(operation["responses"]!["200"]);
        Assert.NotNull(operation["responses"]!["409"]);
        Assert.DoesNotContain(document["paths"]!.AsObject().Select(p => p.Key), path => path.Contains("restore", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> IssueAsync(string role, params Permission[] scopes)
    {
        var issued = await _api.Host.Shared.ApiTokens.IssueAsync(
            new CommandOrigin(Actor.ForUser(_api.UserOf(role)), Channel.Api, IPAddress.Loopback), new IssueApiTokenRequest("backup", [.. scopes]), Ct);
        return issued.Issued?.Token ?? throw new InvalidOperationException($"No token: {issued.Status} {issued.Problem}");
    }

    private async Task<HttpResponseMessage> BearerAsync(string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(_api.BaseAddress, "/api/v1/backup")) { Content = Body() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _api.Client.SendAsync(request, Ct);
    }
}
