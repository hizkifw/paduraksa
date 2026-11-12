using System.Text;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Data.Commands;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Data.Yaml;

public sealed class ConfigImportServiceTests : IAsyncLifetime
{
    private const string Header = "apiVersion: paduraksa.dev/v1\nkind: Config\n";

    private const string TwoHosts = """
        upstreams:
          - name: app
            destinations:
              - address: http://app:8080
        hosts:
          - names: [a.example.com]
            routes:
              - upstream: app
          - names: [b.example.com]
            kind: respond
            respond: {statusCode: 200, body: ok}
        """;

    private ConfigServiceHarness _h = null!;
    private ConfigImportService _import = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await ConfigServiceHarness.CreateAsync();
        _import = ImportOn(_h);
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static ConfigImportService ImportOn(ConfigServiceHarness harness, Paduraksa.Application.Identity.IPermissionEvaluator? permissions = null) =>
        new(harness.Service, new RevisionQueryService(harness.Db.Factory), harness.Passwords, permissions ?? TestPermissions.AllowAll);

    private Task<ConfigCommandResult> ImportAsync(string yaml, ConfigImportMode mode, ConfigImportOptions? options = null, ConfigServiceHarness? harness = null) =>
        ImportOn(harness ?? _h).ImportAsync(Encoding.UTF8.GetBytes(Header + yaml), mode, ConfigServiceHarness.Admin, options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReplacingAnEmptyConfigCommitsOneAuditedRevisionWithANote()
    {
        var result = await ImportAsync(TwoHosts, ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var current = await _h.CurrentAsync();
        Assert.Equal(["a.example.com", "b.example.com"], current.Hosts.Select(static h => h.Names[0]).Order());
        Assert.Equal(current.Upstreams[0].Id, current.Hosts.Single(static h => h.Kind == HostKind.Proxy).Routes[0].UpstreamId);
        var revision = Assert.Single(await _h.RevisionsAsync());
        Assert.Equal("YAML import (replace)", revision.Message);
        Assert.Contains(await _h.AuditAsync(), static e => e.Action == "config.replace" && e.Outcome == AuditOutcome.Success);
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task ADryRunReturnsTheDiffAndCommitsNothing()
    {
        var result = await ImportAsync(TwoHosts, ConfigImportMode.Replace, new ConfigImportOptions(DryRun: true));

        Assert.Equal(ConfigCommandStatus.DryRun, result.Status);
        Assert.Equal(3, result.Changes.Count);
        Assert.Empty(await _h.RevisionsAsync());
        Assert.Empty(await _h.AuditAsync());
    }

    [Fact]
    public async Task ImportingTheSameYamlAgainChangesNothingInEitherMode()
    {
        await ImportAsync(TwoHosts, ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Unchanged, (await ImportAsync(TwoHosts, ConfigImportMode.Replace)).Status);
        Assert.Equal(ConfigCommandStatus.Unchanged, (await ImportAsync(TwoHosts, ConfigImportMode.Merge)).Status);
        Assert.Single(await _h.RevisionsAsync());
    }

    [Fact]
    public async Task MergingAddsAndUpdatesAndDeletesNothingWhileReplacingDeletesWhatTheYamlLeavesOut()
    {
        await ImportAsync(TwoHosts, ConfigImportMode.Replace);
        var before = await _h.CurrentAsync();
        var untouched = before.Hosts.Single(static h => h.Names[0] == "a.example.com");

        var merged = await ImportAsync("hosts:\n  - names: [b.example.com]\n    kind: respond\n    respond: {statusCode: 503}\n  - names: [c.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Merge);

        Assert.Equal(ConfigCommandStatus.Committed, merged.Status);
        var after = await _h.CurrentAsync();
        Assert.Equal(["a.example.com", "b.example.com", "c.example.com"], after.Hosts.Select(static h => h.Names[0]).Order());
        Assert.Equal(503, after.Hosts.Single(static h => h.Names[0] == "b.example.com").Respond!.StatusCode);
        Assert.Equal(untouched.Version, after.Hosts.Single(static h => h.Names[0] == "a.example.com").Version);
        Assert.Equal(before.Hosts.Single(static h => h.Names[0] == "b.example.com").Id, after.Hosts.Single(static h => h.Names[0] == "b.example.com").Id);
        Assert.Single(after.Upstreams);

        var replaced = await ImportAsync("hosts:\n  - names: [c.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Committed, replaced.Status);
        var last = await _h.CurrentAsync();
        Assert.Equal(["c.example.com"], last.Hosts.Select(static h => h.Names[0]));
        Assert.Empty(last.Upstreams);
        Assert.Equal(3, (await _h.RevisionsAsync()).Count);
    }

    [Fact]
    public async Task MergeKeepsTheStoredGlobalSettingsUnlessTheYamlWritesThem()
    {
        await ImportAsync("settings:\n  unknownHost: close\n", ConfigImportMode.Replace);

        await ImportAsync("hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Merge);
        Assert.Equal(UnknownHostBehaviour.Close, (await _h.CurrentAsync()).Settings.UnknownHost);

        await ImportAsync("settings:\n  unknownHost: misdirectedRequest\n", ConfigImportMode.Merge);
        Assert.Equal(UnknownHostBehaviour.MisdirectedRequest, (await _h.CurrentAsync()).Settings.UnknownHost);

        await ImportAsync("hosts: []\n", ConfigImportMode.Replace);
        Assert.Equal(UnknownHostBehaviour.NotFound, (await _h.CurrentAsync()).Settings.UnknownHost);
    }

    [Theory]
    [InlineData("settings: null\n")]
    [InlineData("settings:\n")]
    [InlineData("settings: ~\n")]
    public async Task AMergeWithNullSettingsKeepsTheStoredSettings(string settings)
    {
        await ImportAsync("settings:\n  unknownHost: close\n", ConfigImportMode.Replace);

        var result = await ImportAsync(settings + "hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Merge);

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Equal(UnknownHostBehaviour.Close, (await _h.CurrentAsync()).Settings.UnknownHost);
    }

    [Fact]
    public async Task ValidationErrorsPointAtTheYamlEntryAndKeyWithItsLine()
    {
        var result = await ImportAsync(
            "hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n  - names: [A.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n",
            ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/hosts/1/names/0", error.Path);
        Assert.Contains("line 7", error.Message, StringComparison.Ordinal);
        Assert.Empty(await _h.RevisionsAsync());
        Assert.Contains(await _h.AuditAsync(), static e => e.Outcome == AuditOutcome.Invalid);
    }

    [Fact]
    public async Task AMergeErrorInANewEntryPointsIntoTheYamlAndOneInAStoredObjectIsARelatedError()
    {
        await ImportAsync("hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Replace);

        var result = await ImportAsync("hosts:\n  - names: [x.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n  - names: [a.example.com]\n    id: " + HostId.New() + "\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Merge);

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        // The stored host a.example.com now clashes with the second entry, which is the one the error is reported on.
        Assert.Contains(result.Errors, static e => e.Path == "/hosts/1/names/0");
    }

    [Fact]
    public async Task ACompileErrorIsMappedBackFromTheCanonicalOrderToTheYaml()
    {
        _h.Compiler.Fail = document => [new ConfigError($"/hosts/{document.Hosts.ToList().FindIndex(static h => h.Names[0] == "z.example.com")}", ConfigErrorCodes.CompileFailed, "refused")];

        var result = await ImportAsync(
            "hosts:\n  - names: [y.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n  - names: [z.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n  - names: [x.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n",
            ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Equal("/hosts/1", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public async Task AYamlErrorIsInvalidWithItsPointerAndTouchesNothing()
    {
        var result = await ImportAsync("hosts:\n  - names: [a.example.com]\n    profile: nope\n", ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Equal("/hosts/0/profile", Assert.Single(result.Errors).Path);
        Assert.Empty(await _h.RevisionsAsync());
    }

    [Fact]
    public async Task ACredentialTheVaultDoesntHoldIsRefusedAndADryRunReportsItAsAnErrorToo()
    {
        var credential = PasswordCredentialId.New();
        var yaml = $"accessLists:\n  - name: staff\n    basicAuth:\n      users:\n        - username: alice\n          credentialId: {credential}\n";

        var refused = await ImportAsync(yaml, ConfigImportMode.Replace);
        var dryRun = await ImportAsync(yaml, ConfigImportMode.Replace, new ConfigImportOptions(DryRun: true));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        var error = Assert.Single(refused.Errors);
        Assert.Equal("/accessLists/0/basicAuth/users/0/credentialId", error.Path);
        Assert.Equal(YamlErrorCodes.CredentialUnknown, error.Code);
        Assert.Empty(await _h.RevisionsAsync());
        Assert.Equal(ConfigCommandStatus.Invalid, dryRun.Status);
        Assert.Equal(YamlErrorCodes.CredentialUnknown, Assert.Single(dryRun.Errors).Code);
        Assert.NotEmpty(dryRun.Changes);
    }

    [Fact]
    public async Task ACredentialTheVaultHoldsOrTheLiveConfigAlreadyNamesIsAccepted()
    {
        var held = PasswordCredentialId.New();
        await _h.Passwords.SetAsync(held, "hunter2".AsMemory(), TestContext.Current.CancellationToken);
        var yaml = $"accessLists:\n  - name: staff\n    basicAuth:\n      users:\n        - username: alice\n          credentialId: {held}\n";

        Assert.Equal(ConfigCommandStatus.Committed, (await ImportAsync(yaml, ConfigImportMode.Replace)).Status);

        // The vault loses it; the config still names it, so exporting and importing again is no new problem.
        await _h.Passwords.DeleteAsync(held, TestContext.Current.CancellationToken);
        Assert.Equal(ConfigCommandStatus.Unchanged, (await ImportAsync(yaml, ConfigImportMode.Replace)).Status);
    }

    [Fact]
    public async Task AUserWithoutACredentialKeepsTheStoredOneOfThatNameAndNoPasswordIsInTheDocument()
    {
        var held = PasswordCredentialId.New();
        await _h.Passwords.SetAsync(held, "hunter2".AsMemory(), TestContext.Current.CancellationToken);
        await ImportAsync($"accessLists:\n  - name: staff\n    basicAuth:\n      users:\n        - username: alice\n          credentialId: {held}\n", ConfigImportMode.Replace);

        var result = await ImportAsync("accessLists:\n  - name: staff\n    notes: edited\n    basicAuth:\n      users:\n        - username: alice\n", ConfigImportMode.Replace);

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Equal(held, (await _h.CurrentAsync()).AccessLists[0].BasicAuth!.Users[0].CredentialId);
    }

    [Fact]
    public async Task WithoutTheWritePermissionNothingIsReadAndNothingIsLearned()
    {
        var denied = ImportOn(_h, TestPermissions.Holding(Permission.ConfigRead));

        var result = await denied.ImportAsync(Encoding.UTF8.GetBytes(Header + "hosts: [{x"), ConfigImportMode.Replace, ConfigServiceHarness.Admin, null, TestContext.Current.CancellationToken);

        Assert.Equal(ConfigCommandStatus.Denied, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AStaleBaseRevisionConflictsInsteadOfOverwriting()
    {
        await ImportAsync(TwoHosts, ConfigImportMode.Replace);
        var stale = (await _h.RevisionsAsync())[0].Id;
        await ImportAsync("hosts:\n  - names: [c.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigImportMode.Merge);

        var result = await ImportAsync(TwoHosts, ConfigImportMode.Replace, new ConfigImportOptions(BaseRevision: stale));

        Assert.Equal(ConfigCommandStatus.Conflict, result.Status);
        Assert.Equal(2, (await _h.RevisionsAsync()).Count);
    }

    [Fact]
    public async Task AYamlTooLargeOrNotYamlIsInvalidAndTouchesNothing()
    {
        var tooLarge = await ImportOn(_h).ImportAsync(new byte[YamlLimits.Default.MaxBytes + 1], ConfigImportMode.Replace, ConfigServiceHarness.Admin, null, TestContext.Current.CancellationToken);
        var junk = await ImportAsync("a: !!python/object:os.system x\n", ConfigImportMode.Replace);

        Assert.Equal(YamlErrorCodes.TooLarge, Assert.Single(tooLarge.Errors).Code);
        Assert.Equal(YamlErrorCodes.Tag, Assert.Single(junk.Errors).Code);
        Assert.Empty(await _h.RevisionsAsync());
    }

    [Fact]
    public async Task ExportIsTheCurrentRevisionOrAPastOneAndNullForAnUnknownOne()
    {
        var empty = await _import.ExportAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(empty!.Revision);

        await ImportAsync(TwoHosts, ConfigImportMode.Replace);
        var first = (await _h.RevisionsAsync())[0];
        await ImportAsync("hosts: []\n", ConfigImportMode.Replace);

        var current = await _import.ExportAsync(cancellationToken: TestContext.Current.CancellationToken);
        var past = await _import.ExportAsync(first.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, current!.Revision!.Seq);
        Assert.DoesNotContain("a.example.com", current.Yaml, StringComparison.Ordinal);
        Assert.Equal(1, past!.Revision!.Seq);
        Assert.Contains("a.example.com", past.Yaml, StringComparison.Ordinal);
        Assert.Null(await _import.ExportAsync(ConfigRevisionId.New(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportingAndImportingInReplaceModeOnAFreshStoreGivesTheSameRevisionHash()
    {
        var credential = PasswordCredentialId.New();
        await _h.Passwords.SetAsync(credential, "hunter2".AsMemory(), TestContext.Current.CancellationToken);
        var rich = Header + $$"""
            settings:
              unknownHost: close
              defaults:
                hsts: {enabled: true, maxAge: 31536000}
            profiles:
              - name: media
                settings: {forceHttps: true}
            rateLimitPolicies:
              - name: api
                requests: 100
            accessLists:
              - name: staff
                ip: {allow: [10.0.0.0/8]}
                basicAuth:
                  users:
                    - username: alice
                      credentialId: {{credential}}
            ruleSets:
              - name: no-scanners
                rules:
                  - action: deny
                    match:
                      userAgent:
                        contains: [sqlmap]
            upstreams:
              - name: jelly
                destinations:
                  - address: http://jelly:8096
            hosts:
              - names: [jelly.example.com, '*.jelly.example.com']
                profile: media
                routes:
                  - upstream: jelly
                    settings:
                      rateLimit: {policy: api}
                      accessList: {accessList: staff}
              - names: [old.example.com]
                kind: redirect
                redirect: {target: "https://jelly.example.com"}
            """;
        Assert.Equal(ConfigCommandStatus.Committed, (await _import.ImportAsync(Encoding.UTF8.GetBytes(rich), ConfigImportMode.Replace, ConfigServiceHarness.Admin, null, TestContext.Current.CancellationToken)).Status);
        var exported = (await _import.ExportAsync(cancellationToken: TestContext.Current.CancellationToken))!;
        var hashA = (await _h.RevisionsAsync())[0].DocumentHash.ToArray();

        await using var fresh = await ConfigServiceHarness.CreateAsync();
        await fresh.Passwords.SetAsync(credential, "hunter2".AsMemory(), TestContext.Current.CancellationToken);
        var imported = await ImportOn(fresh).ImportAsync(Encoding.UTF8.GetBytes(exported.Yaml), ConfigImportMode.Replace, ConfigServiceHarness.Admin, null, TestContext.Current.CancellationToken);

        Assert.Equal(ConfigCommandStatus.Committed, imported.Status);
        var hashB = (await fresh.RevisionsAsync())[0].DocumentHash.ToArray();
        Assert.Equal(Convert.ToHexString(hashA), Convert.ToHexString(hashB));
        Assert.Equal(
            Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(await _h.CurrentAsync())),
            Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(await fresh.CurrentAsync())));
        // And exporting the copy gives the very same text.
        Assert.Equal(exported.Yaml, (await ImportOn(fresh).ExportAsync(cancellationToken: TestContext.Current.CancellationToken))!.Yaml);
    }
}
