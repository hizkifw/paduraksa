using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Paduraksa.Mcp;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The refused list against the real API description (docs/design/admin-surfaces.md "MCP in code mode (M10)"). An endpoint that looks sensitive (a path with a backup, a token, a user, a
/// secret, a credential in it) must be classified by someone: on the refused list, or on the reviewed list with the reason it is allowed. A new
/// endpoint that matches and is on neither fails here, so it cannot slip into what a script may call.
/// </summary>
public sealed class McpRequestPolicyApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static readonly Regex Sensitive = new(EndpointRules.SensitivePath, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<byte[]> SpecAsync() => shared.OpenApiAsync();

    private static List<(string Method, string Path)> OperationsOf(byte[] openApi)
    {
        using var document = JsonDocument.Parse(openApi);
        return
        [
            .. document.RootElement.GetProperty("paths").EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject().Select(method => (Method: method.Name.ToUpperInvariant(), Path: path.Name)))
                .Where(o => o.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE"),
        ];
    }

    /// <summary>What the guard reports: the operations whose path looks sensitive and are on neither list.</summary>
    private static List<string> Unclassified(IEnumerable<(string Method, string Path)> operations) =>
    [
        .. operations
            .Where(o => Sensitive.IsMatch(o.Path))
            .Where(o => !EndpointRules.Refused.Any(r => r.Covers(o.Method, o.Path)) && !EndpointRules.Reviewed.Any(r => r.Covers(o.Method, o.Path)))
            .Select(o => $"{o.Method} {o.Path}"),
    ];

    [Fact]
    public async Task EveryOperationWhosePathLooksSensitiveIsOnTheRefusedListOrTheReviewedOne()
    {
        var unclassified = Unclassified(OperationsOf(await SpecAsync()));
        Assert.True(unclassified.Count == 0, "Classify these in EndpointRules (refused, or reviewed with the reason): " + string.Join("; ", unclassified));
    }

    [Fact]
    public void TheGuardCatchesANewSensitiveEndpointThatNobodyClassified()
    {
        (string, string)[] added =
        [
            ("GET", "/api/v1/vault/keys"),
            ("POST", "/api/v1/backups"),
            ("DELETE", "/api/v1/access-tokens/{id}"),
            ("POST", "/api/v1/secrets"),
            ("PUT", "/api/v1/acme-accounts/{id}"),
            ("GET", "/api/v1/credentials"),
            ("POST", "/api/v1/config/import"),
        ];
        Assert.Equal(
            ["GET /api/v1/vault/keys", "POST /api/v1/backups", "DELETE /api/v1/access-tokens/{id}", "POST /api/v1/secrets", "PUT /api/v1/acme-accounts/{id}", "GET /api/v1/credentials"],
            Unclassified(added));
    }

    [Fact]
    public async Task NoOperationIsBothRefusedAndReviewedAndEveryRuleForAnEndpointThatExistsCoversOne()
    {
        var operations = OperationsOf(await SpecAsync());
        Assert.DoesNotContain(operations, o => EndpointRules.Refused.Any(r => r.Covers(o.Method, o.Path)) && EndpointRules.Reviewed.Any(r => r.Covers(o.Method, o.Path)));
        foreach (var rule in EndpointRules.Refused.Concat(EndpointRules.Reviewed).Concat(EndpointRules.ReadOnlyPosts).Where(r => r.InSpec))
        {
            Assert.True(operations.Any(o => rule.Covers(o.Method, o.Path)), $"{rule.Method} {rule.Path} covers no operation of the API description: remove it, or mark it InSpec: false until the endpoint exists.");
        }
    }

    [Fact]
    public async Task ThePolicyClassifiesEveryOperationAsTheListsSayAndASecretBearingOrAccountOperationIsNeverAllowed()
    {
        var spec = await SpecAsync();
        using var document = JsonDocument.Parse(spec);
        var policy = RequestPolicy.FromSpec(document.RootElement);
        var refused = new List<string>();
        foreach (var (method, template) in OperationsOf(spec))
        {
            var path = Regex.Replace(template, "\\{[^}]+\\}", "x_1");
            var decision = policy.Decide(method, path);
            var onRefusedList = EndpointRules.Refused.Any(r => r.Covers(method, template));
            Assert.Equal(onRefusedList, decision.Kind == RequestKind.Refused);
            if (decision.Kind == RequestKind.Refused)
            {
                refused.Add($"{method} {template}");
                continue;
            }

            Assert.Equal(method == "GET" || EndpointRules.ReadOnlyPosts.Any(r => r.Covers(method, template)) ? RequestKind.Read : RequestKind.Write, decision.Kind);
        }

        // The classes of operation the brief names are all refused, however the lists are edited.
        foreach (var must in new[]
        {
            "POST /api/v1/tokens", "GET /api/v1/tokens", "DELETE /api/v1/tokens/{id}", "GET /api/v1/users", "POST /api/v1/users", "PATCH /api/v1/users/{id}", "GET /api/v1/sessions",
            "POST /api/v1/me/password", "POST /api/v1/auth/login", "POST /api/v1/setup", "POST /api/v1/dns-providers", "POST /api/v1/dns-providers/{id}/credential", "POST /api/v1/acme-accounts",
            "POST /api/v1/notification-channels", "PUT /api/v1/notification-channels/{id}", "POST /api/v1/certificates", "PUT /api/v1/certificates/{id}", "GET /api/v1/logs/tail", "GET /api/v1/metrics",
        })
        {
            Assert.Contains(must, refused);
        }
    }

    [Fact]
    public async Task EveryConfigWriteIsADryRunWithoutConfirmAndNoOtherWriteIs()
    {
        var spec = await SpecAsync();
        using var document = JsonDocument.Parse(spec);
        var policy = RequestPolicy.FromSpec(document.RootElement);
        foreach (var (method, template, takesDryRun) in policy.Operations.Where(o => o.Method != "GET"))
        {
            var decision = policy.Decide(method, Regex.Replace(template, "\\{[^}]+\\}", "x_1"));
            if (decision.Kind == RequestKind.Write)
            {
                Assert.Equal(takesDryRun, decision.SupportsDryRun);
            }
        }

        Assert.True(policy.Decide("PUT", "/api/v1/settings").SupportsDryRun);
        Assert.True(policy.Decide("POST", "/api/v1/revisions/x_1/rollback").SupportsDryRun);
        Assert.False(policy.Decide("POST", "/api/v1/certificates/self-signed").SupportsDryRun);
    }

    [Fact]
    public async Task DraftsAreClassifiedTheReadsAndTheReviewAreAllowedAndTheOtherWritesNeedConfirm()
    {
        using var document = JsonDocument.Parse(await SpecAsync());
        var policy = RequestPolicy.FromSpec(document.RootElement);

        foreach (var (method, path) in new[]
        {
            ("GET", "/api/v1/drafts"), ("GET", "/api/v1/drafts/x_1"), ("GET", "/api/v1/drafts/x_1/config"), ("POST", "/api/v1/drafts/x_1/review"),
        })
        {
            Assert.Equal(RequestKind.Read, policy.Decide(method, path).Kind);
        }

        foreach (var (method, path) in new[]
        {
            ("POST", "/api/v1/drafts"), ("POST", "/api/v1/drafts/x_1/entries"), ("DELETE", "/api/v1/drafts/x_1/entries/1"), ("POST", "/api/v1/drafts/x_1/apply"), ("DELETE", "/api/v1/drafts/x_1"),
        })
        {
            Assert.Equal(RequestKind.Write, policy.Decide(method, path).Kind);
        }

        // An apply has a dry run, which is the review; the others change only the draft and have none.
        Assert.True(policy.Decide("POST", "/api/v1/drafts/x_1/apply").SupportsDryRun);
        Assert.False(policy.Decide("POST", "/api/v1/drafts/x_1/entries").SupportsDryRun);
    }

    [Fact]
    public async Task TheApiDescriptionBuildsIntoAFiniteSpecThatSearchCanWalk()
    {
        await using var tools = CodeModeTools.Create(await SpecAsync(), Paduraksa.Tests.Integration.Mcp.SandboxFixture.Command, TimeSpan.FromMinutes(1));
        var outcome = await tools.SearchAsync(
            "async () => { const seen = new Set(); let refs = 0; const walk = (x) => { if (x === null || typeof x !== 'object' || seen.has(x)) return; seen.add(x); if ('$ref' in x) refs++; for (const k in x) walk(x[k]); }; walk(spec); return { nodes: seen.size, refs, paths: Object.keys(spec.paths).length }; }",
            Ct);
        Assert.False(outcome.IsError, outcome.Text);
        using var result = JsonDocument.Parse(outcome.Text);
        Assert.Equal(0, result.RootElement.GetProperty("refs").GetInt32());
        Assert.InRange(result.RootElement.GetProperty("paths").GetInt32(), 50, 500);
        Assert.InRange(Encoding.UTF8.GetByteCount(outcome.Text), 1, 200);
    }
}
