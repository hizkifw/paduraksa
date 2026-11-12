using System.Diagnostics;
using System.Net;
using System.Text;

using Paduraksa.Admin.Json;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Integration.Admin;

public sealed class ConfigTransferApiTests
{
    private const string Header = "apiVersion: paduraksa.dev/v1\nkind: Config\n";

    private const string Jellyfin = """
        profiles:
          - name: media
            settings: {forceHttps: false}
        upstreams:
          - name: jellyfin
            destinations:
              - address: http://127.0.0.1:8096
        hosts:
          - names: [jellyfin.example.test]
            profile: media
            routes:
              - upstream: jellyfin
          - names: [soon.example.test]
            kind: respond
            respond: {statusCode: 503, body: soon}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Yaml(string body, string contentType = "application/yaml") => new(body, Encoding.UTF8, contentType);

    private static Task<HttpResponseMessage> ImportAsync(AdminApiHarness api, string yaml, string query = "", string? caller = "owner", string? ifMatch = null, string contentType = "application/yaml") =>
        api.SendAsync(HttpMethod.Post, "/api/v1/config/import" + query, caller, ifMatch, Yaml(Header + yaml, contentType));

    private static async Task<ConfigCommandResult> ResultAsync(HttpResponseMessage response) =>
        await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.ConfigCommandResult, Ct);

    private static async Task<string> ExportAsync(AdminApiHarness api, string query = "")
    {
        using var response = await api.GetAsync("/api/v1/config/export" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static async Task<IReadOnlyList<RevisionSummary>> RevisionsAsync(AdminApiHarness api)
    {
        using var response = await api.GetAsync("/api/v1/revisions");
        return (await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.PageRevisionSummary, Ct)).Items;
    }

    [Fact]
    public async Task TheEmptyConfigExportsAsYamlWithNoRevisionAndTheHeaderOfTheFormat()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var response = await api.GetAsync("/api/v1/config/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/yaml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Null(response.Headers.ETag);
        Assert.StartsWith("apiVersion: paduraksa.dev/v1\nkind: Config\n", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportingCommitsOneRevisionTheExportReadsBackAndANoteAndAuditAreKept()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var imported = await ImportAsync(api, Jellyfin, "?mode=replace&message=from%20git");
        var result = await ResultAsync(imported);

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var revision = Assert.Single(await RevisionsAsync(api));
        Assert.Equal("from git", revision.Message);
        Assert.Equal(Channel.Api, revision.Channel);
        var yaml = await ExportAsync(api);
        Assert.Contains("profile: media", yaml, StringComparison.Ordinal);
        Assert.Contains("upstream: jellyfin", yaml, StringComparison.Ordinal);
        using var export = await api.GetAsync("/api/v1/config/export");
        Assert.Equal($"\"{revision.Id}\"", AdminApiHarness.ETag(export));

        using var audit = await api.GetAsync("/api/v1/audit");
        Assert.Contains("config.replace", await audit.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunReturnsTheChangesAndStoresNothing()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var response = await ImportAsync(api, Jellyfin, "?mode=replace&dryRun=true");
        var plan = await ResultAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ConfigCommandStatus.DryRun, plan.Status);
        Assert.Equal(4, plan.Changes.Count);
        Assert.Empty(await RevisionsAsync(api));
    }

    [Fact]
    public async Task MergeIsTheDefaultAndDeletesNothingWhileReplaceDeletesWhatTheYamlLeavesOut()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(api, Jellyfin)).StatusCode);

        using var merged = await ImportAsync(api, "hosts:\n  - names: [new.example.test]\n    kind: respond\n    respond: {statusCode: 200}\n");
        Assert.Equal(ConfigCommandStatus.Committed, (await ResultAsync(merged)).Status);
        var afterMerge = await ExportAsync(api);
        Assert.Contains("jellyfin.example.test", afterMerge, StringComparison.Ordinal);
        Assert.Contains("new.example.test", afterMerge, StringComparison.Ordinal);

        using var replaced = await ImportAsync(api, "hosts:\n  - names: [only.example.test]\n    kind: respond\n    respond: {statusCode: 200}\n", "?mode=replace");
        Assert.Equal(ConfigCommandStatus.Committed, (await ResultAsync(replaced)).Status);
        var afterReplace = await ExportAsync(api);
        Assert.DoesNotContain("jellyfin", afterReplace, StringComparison.Ordinal);
        Assert.Contains("only.example.test", afterReplace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportImportedInReplaceModeOnAFreshInstanceHasTheSameRevisionHash()
    {
        await using var first = await AdminApiHarness.StartAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(first, Jellyfin, "?mode=replace")).StatusCode);
        var exported = await ExportAsync(first);

        await using var second = await AdminApiHarness.StartAsync(Ct);
        using var imported = await second.SendAsync(HttpMethod.Post, "/api/v1/config/import?mode=replace", "owner", null, Yaml(exported));

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal((await RevisionsAsync(first))[0].DocumentSha256, (await RevisionsAsync(second))[0].DocumentSha256);
        Assert.Equal(exported, await ExportAsync(second));
    }

    [Fact]
    public async Task APastRevisionExportsAndAnUnknownOrMalformedOneIsRefused()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await ImportAsync(api, Jellyfin, "?mode=replace");
        var first = (await RevisionsAsync(api))[0];
        await ImportAsync(api, "hosts: []\n", "?mode=replace");

        var past = await ExportAsync(api, $"?revision={first.Id}");
        Assert.Contains("jellyfin.example.test", past, StringComparison.Ordinal);
        Assert.DoesNotContain("jellyfin", await ExportAsync(api), StringComparison.Ordinal);
        using var unknown = await api.GetAsync($"/api/v1/config/export?revision={ConfigRevisionId.New()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var malformed = await api.GetAsync("/api/v1/config/export?revision=nope");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task ErrorsAre422WithPointersAndLinesIntoTheYaml()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var unknownReference = await ImportAsync(api, "hosts:\n  - names: [a.example.test]\n    profile: nope\n", "?mode=replace");
        var reference = await AdminApiHarness.ProblemAsync(unknownReference, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownReference.StatusCode);
        var referenceError = Assert.Single(reference.Errors!);
        Assert.Equal("/hosts/0/profile", referenceError.Path);
        Assert.Contains("line 5", referenceError.Message, StringComparison.Ordinal);

        using var clash = await ImportAsync(api, "hosts:\n  - names: [a.example.test]\n    kind: respond\n    respond: {statusCode: 200}\n  - names: [a.example.test]\n    kind: respond\n    respond: {statusCode: 200}\n", "?mode=replace");
        var clashError = Assert.Single((await AdminApiHarness.ProblemAsync(clash, Ct)).Errors!);
        Assert.Equal("/hosts/1/names/0", clashError.Path);

        using var syntax = await ImportAsync(api, "hosts: [\n", "?mode=replace");
        Assert.Equal(YamlErrorCodes.Syntax, Assert.Single((await AdminApiHarness.ProblemAsync(syntax, Ct)).Errors!).Code);
        Assert.Empty(await RevisionsAsync(api));
    }

    [Fact]
    public async Task AUnknownKeyATagAndAnAliasBombAreRefusedFast()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var unknownKey = await ImportAsync(api, "hosts:\n  - names: [a.example.test]\n    tag: [x]\n");
        Assert.Equal(YamlErrorCodes.KeyUnknown, Assert.Single((await AdminApiHarness.ProblemAsync(unknownKey, Ct)).Errors!).Code);

        using var tag = await ImportAsync(api, "settings: !!python/object:os.system x\n");
        Assert.Equal(YamlErrorCodes.Tag, Assert.Single((await AdminApiHarness.ProblemAsync(tag, Ct)).Errors!).Code);

        StringBuilder bomb = new("a: &a [x, x, x, x, x, x, x, x, x]\n");
        var previous = "a";
        foreach (var next in "bcdefghijk")
        {
            bomb.Append(next).Append(": &").Append(next).Append(" [").Append(string.Join(", ", Enumerable.Repeat('*' + previous, 9))).Append("]\n");
            previous = next.ToString();
        }

        var clock = Stopwatch.StartNew();
        using var bombed = await ImportAsync(api, bomb.ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bombed.StatusCode);
        Assert.Equal(YamlErrorCodes.Nodes, Assert.Single((await AdminApiHarness.ProblemAsync(bombed, Ct)).Errors!).Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AWrongContentTypeABadModeAndABodyOverTheLimitAreRefused()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var json = await ImportAsync(api, "{}", contentType: "application/json");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        using var mode = await ImportAsync(api, string.Empty, "?mode=overwrite");
        Assert.Equal(HttpStatusCode.BadRequest, mode.StatusCode);
        using var dry = await ImportAsync(api, string.Empty, "?dryRun=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, dry.StatusCode);

        using var large = await api.SendAsync(HttpMethod.Post, "/api/v1/config/import", "owner", null, Yaml(Header + "notes: " + new string('x', YamlLimits.Default.MaxBytes)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Empty(await RevisionsAsync(api));
    }

    [Fact]
    public async Task IfMatchPinsTheRevisionTheYamlWasPreparedAgainstAndAStaleOneIs412()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await ImportAsync(api, Jellyfin, "?mode=replace");
        var first = (await RevisionsAsync(api))[0];
        await ImportAsync(api, "hosts:\n  - names: [more.example.test]\n    kind: respond\n    respond: {statusCode: 200}\n");

        using var stale = await ImportAsync(api, Jellyfin, "?mode=replace", ifMatch: $"\"{first.Id}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(2, (await RevisionsAsync(api)).Count);
    }

    [Fact]
    public async Task ReadingNeedsConfigReadAndImportingNeedsConfigWrite()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var viewerExport = await api.GetAsync("/api/v1/config/export", "viewer");
        Assert.Equal(HttpStatusCode.OK, viewerExport.StatusCode);
        using var viewerImport = await ImportAsync(api, Jellyfin, caller: "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, viewerImport.StatusCode);
        using var operatorImport = await ImportAsync(api, Jellyfin, "?dryRun=true", caller: "operator");
        Assert.Equal(HttpStatusCode.OK, operatorImport.StatusCode);
        using var anonymous = await api.GetAsync("/api/v1/config/export", caller: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task ASessionCookieMayImportYamlSameOriginButNotCrossSite()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        var cookie = await api.SignInAsync("owner");

        using var sameOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/config/import?dryRun=true", cookie, fetchSite: "same-origin", content: Yaml(Header + Jellyfin));
        using var crossSite = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/config/import?dryRun=true", cookie, fetchSite: "cross-site", content: Yaml(Header + Jellyfin));
        using var form = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/config/import?dryRun=true", cookie, fetchSite: "same-origin", content: new StringContent(Header, Encoding.UTF8, "application/x-www-form-urlencoded"));

        using var plainText = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/config/import?dryRun=true", cookie, fetchSite: "same-origin", content: Yaml(Header + Jellyfin, "text/plain"));
        using var plainTokenCaller = await ImportAsync(api, Jellyfin, "?dryRun=true", contentType: "text/plain");

        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, form.StatusCode);
        // text/plain is what a cross-site form can send: a cookie may not, a token (curl) may.
        Assert.Equal(HttpStatusCode.Forbidden, plainText.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plainTokenCaller.StatusCode);
    }

    [Fact]
    public async Task APasswordCanNotBeSentAndACredentialTheVaultDoesntHoldIsRefused()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var password = await ImportAsync(api, "accessLists:\n  - name: a\n    basicAuth:\n      users:\n        - username: u\n          password: secret\n");
        var passwordError = Assert.Single((await AdminApiHarness.ProblemAsync(password, Ct)).Errors!);
        Assert.Equal(YamlErrorCodes.KeyUnknown, passwordError.Code);
        Assert.DoesNotContain("secret", passwordError.Message, StringComparison.Ordinal);

        using var credential = await ImportAsync(api, $"accessLists:\n  - name: a\n    basicAuth:\n      users:\n        - username: u\n          credentialId: {PasswordCredentialId.New()}\n");
        Assert.Equal(YamlErrorCodes.CredentialUnknown, Assert.Single((await AdminApiHarness.ProblemAsync(credential, Ct)).Errors!).Code);
    }

    [Fact]
    public async Task AnExportNeverCarriesASecretWhateverTheConfigHolds()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        var credential = PasswordCredentialId.New();
        await api.Host.Shared.PasswordCredentials.SetAsync(credential, "hunter2-never-exported".AsMemory(), Ct);
        using var created = await api.SendAsync(
            HttpMethod.Post,
            "/api/v1/accessLists",
            content: AdminApiHarness.JsonBody($$$"""{"id":"{{{AccessListId.New()}}}","name":"staff","basicAuth":{"users":[{"username":"alice","credentialId":"{{{credential}}}"}]}}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var yaml = await ExportAsync(api);

        Assert.Contains($"credentialId: {credential}", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("password", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$pbkdf2", yaml, StringComparison.Ordinal);
    }
}
