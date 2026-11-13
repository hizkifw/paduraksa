using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Ids;

namespace Paduraksa.Admin.Api;

/// <summary>What kind of value a query parameter carries, for the API description (the handlers still parse and refuse by hand, with a problem that names the parameter).</summary>
internal enum QueryType
{
    Text,
    Integer,
    Boolean,
    Time,
    Choice,
    Id,
}

/// <summary>
/// One query parameter of the API: its name and what the API description says about it. The endpoints read a query only through
/// <see cref="Read"/>, and an endpoint that reads one declares it (<see cref="ApiQueryConventions.WithQuery"/>), so the description can't
/// miss one: <c>HttpRequest.Query</c> is banned in this project outside this file, and reading a parameter an endpoint didn't declare throws.
/// </summary>
internal sealed class QueryParameter
{
    private QueryParameter(string name, string description, QueryType type)
    {
        Name = name;
        Description = description;
        Type = type;
    }

    public string Name { get; }

    public string Description { get; }

    public QueryType Type { get; }

    /// <summary>The ID type, for <see cref="QueryType.Id"/>.</summary>
    public Type? IdType { get; private init; }

    /// <summary>The names the parameter accepts, for <see cref="QueryType.Choice"/>.</summary>
    public IReadOnlyList<string>? Values { get; private init; }

    public int? Minimum { get; private init; }

    public int? Maximum { get; private init; }

    public int? MaxLength { get; private init; }

    /// <summary>What an absent parameter means, written as the value: <c>50</c>, <c>false</c>, <c>hour</c>.</summary>
    public string? Default { get; private init; }

    public static QueryParameter Text(string name, string description, int? maxLength = null) =>
        new(name, description, QueryType.Text) { MaxLength = maxLength };

    public static QueryParameter Integer(string name, string description, int minimum, int maximum, int? defaultValue = null) =>
        new(name, description, QueryType.Integer) { Minimum = minimum, Maximum = maximum, Default = defaultValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    public static QueryParameter Flag(string name, string description) =>
        new(name, description, QueryType.Boolean) { Default = "false" };

    public static QueryParameter Time(string name, string description) =>
        new(name, description, QueryType.Time);

    public static QueryParameter Choice(string name, string description, IEnumerable<string> values, string? defaultValue = null) =>
        new(name, description, QueryType.Choice) { Values = [.. values], Default = defaultValue };

    public static QueryParameter Id<TId>(string name, string description)
        where TId : struct, ITypedId<TId> =>
        new(name, description, QueryType.Id) { IdType = typeof(TId) };

    /// <summary>The parameter's values in the request (empty when absent).</summary>
    public StringValues Read(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.GetEndpoint() is { } endpoint && !endpoint.Metadata.OfType<QueryParameterMetadata>().Any(m => m.Parameter.Name == Name))
        {
            throw new InvalidOperationException($"{endpoint.DisplayName} reads the query parameter '{Name}' but doesn't declare it with WithQuery, so the API description would not mention it.");
        }

#pragma warning disable RS0030 // The one place a query is read: everything else goes through a declared QueryParameter.
        return http.Request.Query[Name];
#pragma warning restore RS0030
    }
}

/// <summary>Endpoint metadata: a query parameter the endpoint reads, which the API description lists.</summary>
internal sealed record QueryParameterMetadata(QueryParameter Parameter);

internal static class ApiQueries
{
    public static QueryParameter Limit { get; } = QueryParameter.Integer(
        "limit", $"How many items a page holds, 1 to {Pagination.MaxLimit} (default {Pagination.DefaultLimit}).", 1, Pagination.MaxLimit, Pagination.DefaultLimit);

    public static QueryParameter Cursor { get; } = QueryParameter.Text(
        "cursor", "The nextCursor of the previous page, to continue after it. An opaque string only the API that issued it understands.", maxLength: 96);

    public static QueryParameter DryRun { get; } = QueryParameter.Flag(
        "dryRun", "true validates and compiles the change and returns the plan without committing it.");

    public static QueryParameter Force { get; } = QueryParameter.Flag(
        "force", "true overrides the admin host's lock-out guard, for a change that would remove the caller's own way in.");

    public static QueryParameter Message { get; } = QueryParameter.Text(
        "message", "A note stored with the revision this change makes (at most 1000 characters).", maxLength: 1000);
}

internal static class ApiQueryConventions
{
    /// <summary>Declares the query parameters the endpoint reads, so the API description lists them.</summary>
    internal static TBuilder WithQuery<TBuilder>(this TBuilder builder, params QueryParameter[] parameters)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var parameter in parameters)
        {
            builder.WithMetadata(new QueryParameterMetadata(parameter));
        }

        return builder;
    }

    /// <summary>Declares <c>limit</c> and <c>cursor</c>, which <see cref="Pagination"/> reads.</summary>
    internal static TBuilder WithPage<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithQuery(ApiQueries.Limit, ApiQueries.Cursor);
}
