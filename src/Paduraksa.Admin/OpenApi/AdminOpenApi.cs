using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
/// The API description: an OpenAPI 3.1 document of every <c>/api/v1</c> endpoint, generated from the endpoints themselves and cached after the
/// first request (the set of endpoints is fixed once the app runs), and the Scalar reference UI over it. The document and the UI describe the API,
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

    /// <summary>The extension member naming the permission an operation requires.</summary>
    public const string PermissionExtension = "x-required-permission";

    public static void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<OpenApiDocumentCache>();
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

    private static Task DescribeDocument(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Paduraksa admin API",
            Version = "v1",
            Description = "The REST API of the Paduraksa admin app. Authenticate with an API token (Authorization: Bearer ...) or a signed-in session. "
                + "Every error is RFC 9457 problem details (application/problem+json) with a traceId. "
                + "Config objects (hosts, routes, upstreams, profiles, the settings) and rollbacks take optimistic concurrency: a read carries an ETag (an object's version, or the latest revision's ID), "
                + "and a change sends it back as one quoted tag in If-Match. Without it the answer is 428; a stale or weak tag is 412 (the problem's conflict says what is current); "
                + "an unquoted tag, a list or '*' is 400. Creating with POST needs none. Changes to config objects also take ?dryRun=true, which returns the plan and commits nothing. "
                + "A change that was stored but could not go live is 500 with code config.publishFailed, the stored revision and the new ETag. "
                + "Each operation names the permission it needs (x-required-permission).",
        };
        document.Servers = [];
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
/// The generated document, serialized once: the endpoints don't change while the app runs, and generating it walks every schema. A
/// generation that fails is not kept: the next request starts it again, so one transient failure doesn't leave the description
/// unavailable for the life of the process.
/// </summary>
internal sealed class OpenApiDocumentCache(IServiceProvider services)
{
    private readonly Lock _gate = new();
    private Lazy<Task<byte[]>>? _document;

    public Task<byte[]> GetAsync(CancellationToken cancellationToken)
    {
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

    private static async Task<byte[]> GenerateAsync(IServiceProvider services)
    {
        var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(AdminOpenApi.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None).ConfigureAwait(false);
        using MemoryStream buffer = new();
        await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_1, CancellationToken.None).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
