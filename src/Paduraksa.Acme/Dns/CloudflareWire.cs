using System.Text.Json.Serialization;

namespace Paduraksa.Acme.Dns;

// The JSON shapes of the Cloudflare v4 API that the provider reads and writes. Listed here and source-generated; nothing is
// serialized by reflection.

/// <summary>The source-generated JSON context for the Cloudflare API. Names are snake_case, as Cloudflare spells them.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareZone>>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareRecord>>))]
[JsonSerializable(typeof(CloudflareEnvelope<CloudflareTokenStatus>))]
[JsonSerializable(typeof(CloudflareEnvelope<CloudflareRecord>))]
[JsonSerializable(typeof(CloudflareEnvelope<object>))]
[JsonSerializable(typeof(CloudflareRecordWrite))]
internal sealed partial class CloudflareJsonContext : JsonSerializerContext;

/// <summary>Every Cloudflare answer: <c>success</c>, the <c>errors</c> and the <c>result</c>.</summary>
internal sealed class CloudflareEnvelope<T>
{
    public bool Success { get; init; }

    public List<CloudflareError>? Errors { get; init; }

    public T? Result { get; init; }

    public CloudflareResultInfo? ResultInfo { get; init; }

    /// <summary>The first error as a message part (<c>Authentication error (10000)</c>), or null.</summary>
    public string? FirstError => Errors is { Count: > 0 } errors ? $"{DnsApiClient.Clean(errors[0].Message)} ({errors[0].Code})" : null;

    public bool HasError(int code) => Errors?.Any(e => e.Code == code) == true;
}

internal sealed class CloudflareError
{
    public int Code { get; init; }

    public string? Message { get; init; }
}

internal sealed class CloudflareResultInfo
{
    public int Page { get; init; }

    public int TotalPages { get; init; }
}

internal sealed class CloudflareZone
{
    public string? Id { get; init; }

    public string? Name { get; init; }
}

internal sealed class CloudflareRecord
{
    public string? Id { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Content { get; init; }
}

internal sealed class CloudflareTokenStatus
{
    public string? Status { get; init; }
}

/// <summary>The body of a record creation.</summary>
internal sealed class CloudflareRecordWrite
{
    public required string Type { get; init; }

    public required string Name { get; init; }

    public required string Content { get; init; }

    public int Ttl { get; init; }
}
