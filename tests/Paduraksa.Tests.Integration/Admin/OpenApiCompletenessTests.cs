using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Api;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The API description is what agents and clients discover the API from, so what it leaves out can't be found. These tests walk the generated description and the
/// endpoints it comes from, and fail for an operation that reads a query parameter it doesn't declare, answers a success without a schema or lists no problem it can give.
/// </summary>
public sealed partial class OpenApiCompletenessTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch"];

    private async Task<JsonElement> DocumentAsync()
    {
        using var document = JsonDocument.Parse(await shared.OpenApiAsync());
        return document.RootElement.Clone();
    }

    private static IEnumerable<(string Method, string Path, JsonElement Operation)> Operations(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Where(m => Methods.Contains(m.Name)).Select(m => (m.Name.ToUpperInvariant(), path.Name, m.Value)));

    private static string Id(string method, string path) => $"{method} {path}";

    private static List<string> QueryNames(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? [.. parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query").Select(p => p.GetProperty("name").GetString()!)]
            : [];

    [Fact]
    public async Task EveryQueryParameterAnEndpointReadsIsDeclaredInTheDescriptionAndNoOtherIs()
    {
        var document = await DocumentAsync();
        var described = Operations(document).ToDictionary(o => Id(o.Method, o.Path), o => QueryNames(o.Operation).Order(StringComparer.Ordinal).ToList());

        var endpoints = ((IEndpointRouteBuilder)shared.Api.Host.Admin).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } text && text.StartsWith("/api/v1/", StringComparison.Ordinal) && e.RoutePattern.RawText != "/api/v1/openapi.json")
            .ToList();
        Assert.True(endpoints.Count > 100, "the walk found the API's endpoints");

        List<string> wrong = [];
        foreach (var endpoint in endpoints)
        {
            var path = Normalize(endpoint.RoutePattern.RawText!);
            var declared = endpoint.Metadata.OfType<QueryParameterMetadata>().Select(m => m.Parameter.Name).Order(StringComparer.Ordinal).ToList();
            foreach (var method in endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                if (!described.TryGetValue(Id(method, path), out var listed))
                {
                    wrong.Add($"{Id(method, path)} is mapped and not in the description");
                }
                else if (!listed.SequenceEqual(declared))
                {
                    wrong.Add($"{Id(method, path)} declares [{string.Join(",", declared)}] and the description lists [{string.Join(",", listed)}]");
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task EveryQueryParameterHasADescriptionAndATypeAndAnEnumOrARangeWhereItHasOne()
    {
        var document = await DocumentAsync();
        List<string> wrong = [];
        foreach (var (method, path, operation) in Operations(document))
        {
            if (!operation.TryGetProperty("parameters", out var parameters))
            {
                continue;
            }

            foreach (var parameter in parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query"))
            {
                var name = parameter.GetProperty("name").GetString();
                if (!parameter.TryGetProperty("description", out var description) || string.IsNullOrWhiteSpace(description.GetString()))
                {
                    wrong.Add($"{Id(method, path)} ?{name} has no description");
                }

                var schema = parameter.GetProperty("schema");
                if (!schema.TryGetProperty("type", out _) && !schema.TryGetProperty("$ref", out _))
                {
                    wrong.Add($"{Id(method, path)} ?{name} has no type");
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task EverySuccessThatSendsABodyDescribesItWithASchemaAndA204SendsNone()
    {
        var document = await DocumentAsync();
        List<string> wrong = [];
        foreach (var (method, path, operation) in Operations(document).Where(o => o.Method != "HEAD"))
        {
            var successes = operation.GetProperty("responses").EnumerateObject().Where(r => r.Name.StartsWith('2')).ToList();
            if (successes.Count == 0)
            {
                wrong.Add($"{Id(method, path)} documents no success");
            }

            foreach (var response in successes)
            {
                var hasContent = response.Value.TryGetProperty("content", out var content);
                if (response.Name == "204")
                {
                    if (hasContent)
                    {
                        wrong.Add($"{Id(method, path)} 204 lists a body");
                    }

                    continue;
                }

                if (!hasContent || !content.EnumerateObject().Any())
                {
                    wrong.Add($"{Id(method, path)} {response.Name} lists no body");
                    continue;
                }

                foreach (var media in content.EnumerateObject().Where(m => !m.Value.TryGetProperty("schema", out _)))
                {
                    wrong.Add($"{Id(method, path)} {response.Name} {media.Name} has no schema");
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task EveryOperationListsTheProblemsItCanGiveEachWithTheProblemSchema()
    {
        var document = await DocumentAsync();
        List<string> wrong = [];
        foreach (var (method, path, operation) in Operations(document))
        {
            var responses = operation.GetProperty("responses").EnumerateObject().ToDictionary(r => r.Name, r => r.Value);
            var anonymous = operation.TryGetProperty("security", out var security) && security.GetArrayLength() == 0;
            var hasBody = operation.TryGetProperty("requestBody", out _);
            var hasPathParameter = Regex.IsMatch(path, "\\{[^}]+\\}");
            var queries = QueryNames(operation);

            void Expect(string status, string why)
            {
                if (!responses.ContainsKey(status))
                {
                    wrong.Add($"{Id(method, path)} lists no {status} ({why})");
                }
            }

            Expect("500", "any operation may fail unexpectedly");
            if (!anonymous)
            {
                Expect("401", "it needs a caller");
            }

            if (hasBody || queries.Count > 0)
            {
                Expect("400", "it reads a body or a query parameter it can refuse");
            }

            if (hasBody)
            {
                Expect("415", "it reads a body");
            }

            if (hasPathParameter && method != "HEAD")
            {
                Expect("404", "it names an object in its path");
            }

            if (operation.TryGetProperty("x-required-permission", out _))
            {
                Expect("403", "it needs a permission");
            }

            if (queries.Contains("dryRun") && (method is "PUT" or "DELETE" || hasPathParameter))
            {
                Expect("412", "it changes a versioned object");
                Expect("428", "it changes a versioned object");
            }

            foreach (var response in responses.Where(r => r.Key[0] is '4' or '5'))
            {
                var problem = response.Value.TryGetProperty("content", out var content) && content.TryGetProperty("application/problem+json", out var media)
                    && media.TryGetProperty("schema", out var schema) && schema.TryGetProperty("$ref", out var reference) && reference.GetString()!.EndsWith("/ApiProblem", StringComparison.Ordinal);
                if (!problem)
                {
                    wrong.Add($"{Id(method, path)} {response.Key} is not an ApiProblem as application/problem+json");
                }

                if (response.Value.GetProperty("description").GetString() is "Bad Request" or "Not Found" or "Conflict" or "Precondition Failed" or "Unprocessable Entity" or "Precondition Required" or "Forbidden")
                {
                    wrong.Add($"{Id(method, path)} {response.Key} is described by its reason phrase alone");
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task EveryCollectionThatPagesTakesLimitAndCursorAndAnswersAPageAndTheOtherWayRound()
    {
        var document = await DocumentAsync();
        List<string> wrong = [];
        foreach (var (method, path, operation) in Operations(document).Where(o => o.Method == "GET"))
        {
            var queries = QueryNames(operation);
            var okSchema = operation.GetProperty("responses").TryGetProperty("200", out var ok) && ok.TryGetProperty("content", out var content)
                && content.TryGetProperty("application/json", out var media) && media.TryGetProperty("schema", out var schema) && schema.TryGetProperty("$ref", out var reference)
                ? reference.GetString()!
                : string.Empty;
            var isPage = okSchema.Contains("/PageOf", StringComparison.Ordinal);
            var takesPaging = queries.Contains("limit") && queries.Contains("cursor");
            if (isPage != takesPaging)
            {
                wrong.Add($"{Id(method, path)}: answers a page = {isPage}, takes limit and cursor = {takesPaging}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task TheDescriptionNamesItsServerAndOffersTheVersionEndpoint()
    {
        var document = await DocumentAsync();

        Assert.True(document.GetProperty("servers").GetArrayLength() >= 1);
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/version", out _));
    }

    /// <summary>The route as the description writes it: no route constraints, no trailing slash.</summary>
    private static string Normalize(string route)
    {
        var path = ConstraintPattern().Replace(route, "{$1}");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    [GeneratedRegex("\\{([^}:]+):[^}]+\\}")]
    private static partial Regex ConstraintPattern();
}
