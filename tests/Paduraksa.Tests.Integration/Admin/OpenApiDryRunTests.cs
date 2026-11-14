using System.Text.Json;

using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The API description says which changes have a dry run. Every change that goes through the config pipeline reads <c>?dryRun=</c>, and nothing
/// else does, so the description declares the parameter on exactly the operations that need <c>config:write</c> (or <c>server:write</c>, a
/// change to the server settings, which are part of the config) and are not reads: a client (MCP's
/// request policy among them) learns from the description, with no list of its own, which writes it can preview.
/// </summary>
public sealed class OpenApiDryRunTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The operations that need <c>config:write</c> and change a draft, not the config: they commit nothing, so they declare no dry run. A draft's apply is
    /// not among them (it commits, and its dry run is its review); a new draft operation has to be named here or declare one.
    /// </summary>
    private static readonly HashSet<string> ChangeOnlyADraft = new(StringComparer.Ordinal)
    {
        "post /api/v1/drafts",
        "post /api/v1/drafts/{id}/entries",
        "delete /api/v1/drafts/{id}/entries/{number}",
        "post /api/v1/drafts/{id}/review",
        "delete /api/v1/drafts/{id}",
    };

    private async Task<IReadOnlyList<(string Method, string Path, bool WritesConfig, bool DeclaresDryRun)>> OperationsAsync()
    {
        using var response = await shared.Api.GetAsync("/api/v1/openapi.json", null);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        List<(string, string, bool, bool)> operations = [];
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                var changesOnlyADraft = ChangeOnlyADraft.Contains($"{method.Name} {path.Name}");
                var writesConfig = method.Value.TryGetProperty("x-required-permission", out var permission) && permission.GetString() is "config:write" or "server:write" && method.Name != "get" && !changesOnlyADraft;
                var declares = method.Value.TryGetProperty("parameters", out var parameters)
                    && parameters.EnumerateArray().Any(p => p.GetProperty("name").GetString() == "dryRun" && p.GetProperty("in").GetString() == "query");
                operations.Add((method.Name, path.Name, writesConfig, declares));
            }
        }

        return operations;
    }

    [Fact]
    public async Task EveryConfigWriteDeclaresDryRunAndNothingElseDoes()
    {
        var operations = await OperationsAsync();
        Assert.Contains(operations, o => o is { Method: "put", Path: "/api/v1/settings", DeclaresDryRun: true });
        Assert.Contains(operations, o => o is { Method: "post", Path: "/api/v1/revisions/{id}/rollback", DeclaresDryRun: true });
        Assert.Contains(operations, o => o is { Method: "post", Path: "/api/v1/drafts/{id}/apply", DeclaresDryRun: true });
        Assert.Equal(
            operations.Where(o => o.WritesConfig).Select(o => $"{o.Method} {o.Path}").Order(StringComparer.Ordinal),
            operations.Where(o => o.DeclaresDryRun).Select(o => $"{o.Method} {o.Path}").Order(StringComparer.Ordinal));
    }
}
