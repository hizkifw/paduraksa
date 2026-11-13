using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Auth;
using Paduraksa.Core.Model;

using Scalar.AspNetCore;

namespace Paduraksa.Admin.OpenApi;

/// <summary>
/// The API description: an OpenAPI 3.1 document of every <c>/api/v1</c> endpoint, generated from the endpoints themselves and served from a copy
/// shipped in the assembly (generating it costs seconds of CPU and gigabytes of allocation; see <see cref="OpenApiDocumentCache"/>), and the Scalar reference UI over it. The document and the UI describe the API,
/// not data, so both are anonymous (the UI's try-it requests carry the browser's own session or the token the reader pastes in). Nothing is loaded
/// from the internet: Scalar's script is embedded in its package and served from the admin app, the fonts are the browser's, and telemetry, the AI
/// agent and the MCP hook are off.
/// </summary>
internal static class AdminOpenApi
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/api/v1/openapi.json";
    public const string DocsPath = "/api/docs";
    public const string BearerScheme = "bearer";

    /// <summary>The generated document, embedded from <c>OpenApi/openapi.v1.json</c>; a test keeps it equal to a fresh generation.</summary>
    public const string ShippedDocumentResource = "Paduraksa.Admin.OpenApi.openapi.v1.json";

    /// <summary>The extension member naming the permission an operation requires.</summary>
    public const string PermissionExtension = "x-required-permission";

    public static void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(static provider => new OpenApiDocumentCache(provider, ShippedDocument()));
        services.AddOpenApi(DocumentName, options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.ShouldInclude = description => description.RelativePath is { } path && path.StartsWith("api/v1/", StringComparison.Ordinal);
            options.AddDocumentTransformer(DescribeDocument);
            options.AddSchemaTransformer(DescribeSchema);
            options.AddOperationTransformer(DescribeOperationAsync);
        });
    }

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(DocumentPath, static async (HttpContext http, OpenApiDocumentCache cache) =>
                Results.Bytes(await cache.GetAsync(http.RequestAborted).ConfigureAwait(false), "application/json; charset=utf-8"))
            .WithName("GetOpenApiDocument")
            .ExcludeFromDescription()
            .AllowAnonymousAdmin();

        endpoints.MapScalarApiReference(DocsPath, (options, http) => options
                .WithTitle("Paduraksa admin API")
                .WithOpenApiRoutePattern(DocumentPath)
                .WithNonce(DocsContentSecurityPolicy.NonceOf(http))
                .DisableDefaultFonts()
                .DisableTelemetry()
                .DisableAgent()
                .DisableMcp())
            .AllowAnonymousAdmin();
    }

    /// <summary>The embedded document, or null in a build that has none (the cache then generates it).</summary>
    internal static byte[]? ShippedDocument()
    {
        using var stream = typeof(AdminOpenApi).Assembly.GetManifestResourceStream(ShippedDocumentResource);
        if (stream is null)
        {
            return null;
        }

        var document = new byte[stream.Length];
        stream.ReadExactly(document);
        return document;
    }

    private static Task DescribeDocument(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Paduraksa admin API",
            Version = "v1",
            Description = "The REST API of the Paduraksa admin app. Authenticate with an API token (Authorization: Bearer ...) or a signed-in session. "
                + "Paths are kebab-case; JSON members are camelCase; IDs are typed strings (host_...); times are ISO 8601 in UTC; enums are strings. "
                + "A create is POST to the collection: 201 with Location and the created object. A change is PUT or PATCH: 200 with the object. A delete is 204. "
                + "Every collection takes limit and cursor and answers { items, nextCursor }. "
                + "Every error is RFC 9457 problem details (application/problem+json) with a traceId. "
                + "Config objects (hosts, routes, upstreams, profiles, the settings, ...) and rollbacks take optimistic concurrency: a read carries an ETag (an object's version, or the latest revision's ID), "
                + "and a change sends it back as one quoted tag in If-Match. Without it the answer is 428; a stale or weak tag is 412 (the problem's conflict says what is current); "
                + "an unquoted tag, a list or '*' is 400. Creating with POST needs none, and neither do the objects that carry no version (certificates, ACME accounts, DNS providers, notification channels, users). "
                + "Changes to config objects also take ?dryRun=true, which returns the plan and commits nothing. "
                + "A change that was stored but could not go live is 500 with code config.publishFailed, the stored revision and the new ETag. "
                + "Each operation names the permission it needs (x-required-permission).",
        };
        // Paths in this document carry the /api/v1 prefix, so the server is the admin app's own origin.
        document.Servers = [new OpenApiServer { Url = "/", Description = "The admin app that serves this document." }];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "An API token issued to a user. A signed-in session cookie is accepted as well.",
        };
        return Task.CompletedTask;
    }

    private static Task DescribeSchema(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var nullable = underlying != type || (schema.Type is { } existing && existing.HasFlag(JsonSchemaType.Null));

        if (TypedIdSchemas.TryDescribe(underlying, out var id))
        {
            schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
            schema.Pattern = id.Pattern;
            schema.Examples = [JsonValue.Create(id.Example)];
            schema.Description ??= $"A {id.Name}: '{id.Prefix}_' and 22 base62 characters.";
            schema.Properties?.Clear();
            schema.Format = null;
            return Task.CompletedTask;
        }

        if (underlying.IsEnum && Json.WireEnums.NamesOf(underlying) is { } names)
        {
            schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
            schema.Enum = [.. names.Select(n => (JsonNode)JsonValue.Create(n)!)];
            schema.Format = null;
        }

        return Task.CompletedTask;
    }

    private static async Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        await DescribeParametersAsync(operation, context, cancellationToken).ConfigureAwait(false);
        await AddQueryParametersAsync(operation, context, cancellationToken).ConfigureAwait(false);
        DescribeBodiesWithoutType(operation, context);
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var permission = metadata.OfType<RequiredPermissionMetadata>().FirstOrDefault();
        var anonymous = metadata.OfType<AdminAnonymousMetadata>().Any();
        var authenticated = metadata.OfType<AdminAuthenticatedMetadata>().Any();

        // The identity endpoints are named by their operation, not summarised by hand.
        if (string.IsNullOrWhiteSpace(operation.Summary) && operation.OperationId is { Length: > 0 } operationId)
        {
            operation.Summary = SentenceOf(operationId);
        }

        if (permission is not null)
        {
            var name = permission.Permission.ToName();
            Describe(operation, $"Requires the `{name}` permission.");
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            operation.Extensions[PermissionExtension] = new JsonNodeExtension(JsonValue.Create(name)!);
            Secure(operation, context);
            AddProblem(operation, context, "401", "Authentication is required.");
            AddProblem(operation, context, "403", $"The caller lacks the `{name}` permission.");
            AddProblem(
                operation,
                context,
                "500",
                metadata.OfType<PublishFailureMetadata>().Any()
                    ? "Either an unexpected error (the trace ID finds it in the server log), or code config.publishFailed: the change was stored (revision, and the new version in version and the ETag header) but could not go live; the previous config still serves."
                    : "An unexpected error; the trace ID in the problem finds it in the server log.");
        }
        else if (authenticated)
        {
            Describe(operation, "Requires a signed-in caller; what the caller may do is checked by the operation itself.");
            Secure(operation, context);
            AddProblem(operation, context, "401", "Authentication is required.");
            AddProblem(operation, context, "500", "An unexpected error; the trace ID in the problem finds it in the server log.");
        }
        else if (anonymous)
        {
            Describe(operation, "Anonymous: it needs no credentials.");
            operation.Security = [];
            AddProblem(operation, context, "500", "An unexpected error; the trace ID in the problem finds it in the server log.");
        }

        DescribeProblems(operation);
    }

    /// <summary>
    /// Every query parameter an endpoint reads is declared on it (<see cref="QueryParameter"/>, <c>WithQuery</c>), and this is where the description gets them.
    /// MCP's request policy reads which operations have a dry run from the <c>dryRun</c> parameter.
    /// </summary>
    private static async Task AddQueryParametersAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var declared in context.Description.ActionDescriptor.EndpointMetadata.OfType<QueryParameterMetadata>().Select(m => m.Parameter))
        {
            operation.Parameters ??= [];
            if (operation.Parameters.Any(p => p.In == ParameterLocation.Query && p.Name == declared.Name))
            {
                continue;
            }

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = declared.Name,
                In = ParameterLocation.Query,
                Required = false,
                Description = declared.Description,
                Schema = await QuerySchemaAsync(declared, context, cancellationToken).ConfigureAwait(false),
            });
        }
    }

    private static async Task<IOpenApiSchema> QuerySchemaAsync(QueryParameter parameter, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (parameter is { Type: QueryType.Id, IdType: { } idType })
        {
            return await context.GetOrCreateSchemaAsync(idType, null, cancellationToken).ConfigureAwait(false);
        }

        OpenApiSchema schema = parameter.Type switch
        {
            QueryType.Integer => new() { Type = JsonSchemaType.Integer, Minimum = parameter.Minimum?.ToString(CultureInfo.InvariantCulture), Maximum = parameter.Maximum?.ToString(CultureInfo.InvariantCulture) },
            QueryType.Boolean => new() { Type = JsonSchemaType.Boolean },
            QueryType.Time => new() { Type = JsonSchemaType.String, Format = "date-time" },
            QueryType.Choice => new() { Type = JsonSchemaType.String, Enum = [.. parameter.Values!.Select(v => (JsonNode)JsonValue.Create(v)!)] },
            _ => new() { Type = JsonSchemaType.String, MaxLength = parameter.MaxLength },
        };
        if (parameter.Default is { } value)
        {
            schema.Default = parameter.Type switch
            {
                QueryType.Integer => JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture)),
                QueryType.Boolean => JsonValue.Create(bool.Parse(value)),
                _ => JsonValue.Create(value),
            };
        }

        return schema;
    }

    /// <summary>
    /// A success that sends a file, a text or a stream has no type to describe it by, so ASP.NET lists its content type and no content. Its schema is the kind of body: text
    /// for the text types, binary for the rest. Without one no operation could be said to describe its answer, and a test holds every success to having it.
    /// </summary>
    private static void DescribeBodiesWithoutType(OpenApiOperation operation, OpenApiOperationTransformerContext context)
    {
        if (string.Equals(context.Description.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var declared in context.Description.ActionDescriptor.EndpointMetadata.OfType<IProducesResponseTypeMetadata>().Where(r => r.StatusCode is >= 200 and < 300 and not 204))
        {
            var key = declared.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (operation.Responses is null || !operation.Responses.TryGetValue(key, out var response) || response is not OpenApiResponse concrete)
            {
                continue;
            }

            foreach (var mediaType in declared.ContentTypes)
            {
                concrete.Content ??= new Dictionary<string, OpenApiMediaType>();
                if (!concrete.Content.TryGetValue(mediaType, out var media))
                {
                    concrete.Content[mediaType] = media = new OpenApiMediaType();
                }

                var text = mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType.Contains("yaml", StringComparison.Ordinal) || mediaType.Contains("pem", StringComparison.Ordinal);
                media.Schema ??= text
                    ? new OpenApiSchema { Type = JsonSchemaType.String }
                    : new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
            }
        }
    }

    private static void Describe(OpenApiOperation operation, string sentence) =>
        operation.Description = string.IsNullOrEmpty(operation.Description) ? sentence : $"{operation.Description}\n\n{sentence}";

    private static void Secure(OpenApiOperation operation, OpenApiOperationTransformerContext context)
    {
        if (context.Document is { } document)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [] });
        }
    }

    /// <summary>What each problem status means in this API, for the operations that declare it by status alone (<see cref="ApiEndpointConventions.ProducesApiProblems"/>).</summary>
    private static readonly Dictionary<string, string> ProblemDescriptions = new()
    {
        ["400"] = "The request is malformed: a parameter, header or body is unusable (the problem says which).",
        ["403"] = "The caller may not do this.",
        ["404"] = "There is no such object.",
        ["409"] = "It clashes with what exists, or with something in progress.",
        ["412"] = "The If-Match tag is stale (or weak): what you read is no longer current. The problem says what is.",
        ["413"] = "The body is larger than this operation takes.",
        ["415"] = "The body is not in a type this operation takes.",
        ["422"] = "The request is well-formed but can't be applied; errors lists why, with a pointer into the body.",
        ["428"] = "The change needs an If-Match header with the ETag of what you read.",
        ["429"] = "Too many attempts; Retry-After says when to try again.",
        ["502"] = "The CA refused or could not be reached; nothing here is wrong with the request.",
        ["503"] = "The server can't do this right now; try again shortly.",
    };

    /// <summary>A problem response declared by status alone carries the status's reason phrase as its description; this says what it means here.</summary>
    private static void DescribeProblems(OpenApiOperation operation)
    {
        foreach (var (status, response) in operation.Responses ?? [])
        {
            if (response is OpenApiResponse { Content: { } content } plain && content.ContainsKey(ApiProblems.ContentType)
                && ProblemDescriptions.TryGetValue(status, out var meaning)
                && int.TryParse(status, CultureInfo.InvariantCulture, out var code)
                && string.Equals(plain.Description, Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(code), StringComparison.Ordinal))
            {
                plain.Description = meaning;
            }
        }
    }

    private static void AddProblem(OpenApiOperation operation, OpenApiOperationTransformerContext context, string status, string description)
    {
        operation.Responses ??= [];
        if (context.Document is not { } document)
        {
            return;
        }

        // A declared response keeps its schema; its description is replaced when this call has something more exact to say (the 500 of a change that may fail to publish).
        if (operation.Responses.TryGetValue(status, out var declared))
        {
            if (declared is OpenApiResponse concrete && status == "500")
            {
                concrete.Description = description;
            }

            return;
        }

        // Every operation declares the problem answers it can give (ProducesApiProblems), and the identity endpoints answer with them too, so the component exists.
        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType> { [ApiProblems.ContentType] = new OpenApiMediaType { Schema = new OpenApiSchemaReference("ApiProblem", document) } },
        };
    }

    /// <summary>"RevokeOtherSessions" as "Revoke other sessions".</summary>
    internal static string SentenceOf(string operationId)
    {
        StringBuilder sentence = new();
        for (var i = 0; i < operationId.Length; i++)
        {
            var c = operationId[i];
            if (i > 0 && char.IsUpper(c))
            {
                sentence.Append(' ');
            }

            sentence.Append(i > 0 ? char.ToLower(c, CultureInfo.InvariantCulture) : c);
        }

        return sentence.ToString();
    }

    /// <summary>
    /// Route and query parameters of a typed ID type are described by the ID's schema (a string with the prefix and length), not as a bare string:
    /// ASP.NET describes a parameter that binds through <c>TryParse</c> as <c>string</c> without asking the schema transformer.
    /// </summary>
    private static async Task DescribeParametersAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Parameters is not { Count: > 0 } parameters)
        {
            return;
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is OpenApiParameter parameter
                && context.Description.ParameterDescriptions.FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.Ordinal))?.Type is { } type
                && TypedIdSchemas.TryDescribe(Nullable.GetUnderlyingType(type) ?? type, out _))
            {
                parameter.Schema = await context.GetOrCreateSchemaAsync(Nullable.GetUnderlyingType(type) ?? type, null, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// The API description's bytes. Generating the document from the endpoints is slow and heavy: ASP.NET's generator rebuilds and re-walks the full
/// schema of every request and response type for every operation (the config changes each return the whole plan, so most operations are thousands of
/// nodes), about 3 s of CPU warm and 6 to 9 s on a cold process, with some 2.7 GB of allocation that stalls the proxy that shares the process. Nothing
/// about it varies at run time, so a build ships the result (<c>OpenApi/openapi.v1.json</c>, embedded) and this serves it; a test fails when it no longer
/// equals a fresh generation. Without a shipped copy the document is generated on the first request and kept; a generation that fails is not kept: the
/// next request starts it again, so one transient failure doesn't leave the description unavailable for the life of the process.
/// </summary>
internal sealed class OpenApiDocumentCache(IServiceProvider services, byte[]? shipped = null)
{
    private readonly Lock _gate = new();
    private Lazy<Task<byte[]>>? _document;

    /// <summary>Whether the bytes come from the shipped copy, so no request ever generates the document.</summary>
    public bool ServesShippedDocument => shipped is not null;

    public Task<byte[]> GetAsync(CancellationToken cancellationToken)
    {
        if (shipped is not null)
        {
            return Task.FromResult(shipped);
        }

        Lazy<Task<byte[]>> document;
        lock (_gate)
        {
            if (_document is null || (_document.IsValueCreated && _document.Value.IsFaulted))
            {
                _document = new(() => GenerateAsync(services), LazyThreadSafetyMode.ExecutionAndPublication);
            }

            document = _document;
        }

        return document.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Generates the document from the endpoints, the way the shipped copy was made.</summary>
    internal static async Task<byte[]> GenerateAsync(IServiceProvider services)
    {
        var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(AdminOpenApi.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None).ConfigureAwait(false);
        using MemoryStream buffer = new();
        await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_1, CancellationToken.None).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
