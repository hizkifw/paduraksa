using System.Text.Json;
using System.Text.Json.Serialization;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme;

// The JSON shapes of ACME requests and responses. Every type the client reads or writes is listed here, and nothing is
// serialized by reflection: the context is source-generated. The response shapes are internal and map to the public
// model records, which also carry what is not in the body (a Location URL, a Retry-After).

/// <summary>The source-generated JSON context for all ACME JSON. Names are camelCase and read case-sensitively, as the RFCs spell them (RFC 9773's <c>explanationURL</c> is named explicitly).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(AcmeDirectory))]
[JsonSerializable(typeof(AccountWire))]
[JsonSerializable(typeof(OrderWire))]
[JsonSerializable(typeof(AuthorizationWire))]
[JsonSerializable(typeof(ChallengeWire))]
[JsonSerializable(typeof(ProblemWire))]
[JsonSerializable(typeof(RenewalInfoWire))]
[JsonSerializable(typeof(NewAccountWire))]
[JsonSerializable(typeof(ContactUpdateWire))]
[JsonSerializable(typeof(DeactivateWire))]
[JsonSerializable(typeof(NewOrderWire))]
[JsonSerializable(typeof(FinalizeWire))]
[JsonSerializable(typeof(RevokeWire))]
[JsonSerializable(typeof(JwsFlattened))]
internal sealed partial class AcmeJsonContext : JsonSerializerContext;

/// <summary>Reads and writes <see cref="AcmeStatus"/> as the lowercase ACME strings; an unknown string is <see cref="AcmeStatus.Unknown"/>.</summary>
internal sealed class AcmeStatusConverter : JsonConverter<AcmeStatus>
{
    public override AcmeStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "pending" => AcmeStatus.Pending,
                "ready" => AcmeStatus.Ready,
                "processing" => AcmeStatus.Processing,
                "valid" => AcmeStatus.Valid,
                "invalid" => AcmeStatus.Invalid,
                "deactivated" => AcmeStatus.Deactivated,
                "expired" => AcmeStatus.Expired,
                "revoked" => AcmeStatus.Revoked,
                _ => AcmeStatus.Unknown,
            }
            : throw new JsonException("An ACME status must be a string.");

    public override void Write(Utf8JsonWriter writer, AcmeStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

/// <summary>The checks the mappers share.</summary>
internal static class WireChecks
{
    /// <summary>The URL, which must be absolute: a CA's relative URL would only fail later, far from where it came from.</summary>
    public static Uri Absolute(Uri? url, string what) =>
        url is { IsAbsoluteUri: true } ? url : throw new AcmeProtocolException($"The CA sent {what} that is not an absolute URL.");

    public static Uri? AbsoluteOrNull(Uri? url, string what) => url is null ? null : Absolute(url, what);

    public static AcmeIdentifier Complete(AcmeIdentifier? identifier) =>
        identifier is { IsComplete: true } ? identifier : throw new AcmeProtocolException("The CA sent an identifier without a type or a value.");
}

internal sealed class AccountWire
{
    public AcmeStatus Status { get; init; }

    public List<string>? Contact { get; init; }

    public Uri? Orders { get; init; }

    public AcmeAccount ToModel(AcmeAccountId id, Uri url, bool created) =>
        new(id, url, Status, Contact ?? [], WireChecks.AbsoluteOrNull(Orders, "an orders URL"), created);
}

internal sealed class OrderWire
{
    public AcmeStatus Status { get; init; }

    public DateTimeOffset? Expires { get; init; }

    public List<AcmeIdentifier>? Identifiers { get; init; }

    public List<Uri>? Authorizations { get; init; }

    public Uri? Finalize { get; init; }

    public Uri? Certificate { get; init; }

    public string? Profile { get; init; }

    public string? Replaces { get; init; }

    public ProblemWire? Error { get; init; }

    public AcmeOrder ToModel(Uri url, TimeSpan? retryAfter) => new(
        url,
        Status,
        Expires,
        [.. (Identifiers ?? []).Select(static i => WireChecks.Complete(i))],
        [.. (Authorizations ?? []).Select(static a => WireChecks.Absolute(a, "an authorization URL"))],
        WireChecks.Absolute(Finalize ?? throw new AcmeProtocolException("The order has no finalize URL."), "a finalize URL"),
        WireChecks.AbsoluteOrNull(Certificate, "a certificate URL"),
        Profile,
        Replaces,
        Error?.ToModel(),
        retryAfter);
}

internal sealed class AuthorizationWire
{
    public AcmeIdentifier? Identifier { get; init; }

    public AcmeStatus Status { get; init; }

    public DateTimeOffset? Expires { get; init; }

    public bool Wildcard { get; init; }

    public List<ChallengeWire>? Challenges { get; init; }

    public AcmeAuthorization ToModel(Uri url, TimeSpan? retryAfter) => new(
        url,
        WireChecks.Complete(Identifier),
        Status,
        Expires,
        Wildcard,
        [.. (Challenges ?? []).Select(static c => c.ToModel(null))],
        retryAfter);
}

internal sealed class ChallengeWire
{
    public string? Type { get; init; }

    public Uri? Url { get; init; }

    public AcmeStatus Status { get; init; }

    public string? Token { get; init; }

    public DateTimeOffset? Validated { get; init; }

    public ProblemWire? Error { get; init; }

    public AcmeChallenge ToModel(TimeSpan? retryAfter)
    {
        if (Token is not null && !AcmeChallenges.IsValidToken(Token))
        {
            // The token ends up in a URL path (HTTP-01) and in a DNS value: never accept anything but base64url.
            throw new AcmeProtocolException("The CA sent a challenge token that is not base64url.");
        }

        return new AcmeChallenge(
            Type ?? throw new AcmeProtocolException("A challenge has no type."),
            WireChecks.Absolute(Url ?? throw new AcmeProtocolException("A challenge has no URL."), "a challenge URL"),
            Status,
            Token,
            Validated,
            Error?.ToModel(),
            retryAfter);
    }
}

internal sealed class ProblemWire
{
    public string? Type { get; init; }

    public string? Detail { get; init; }

    public int? Status { get; init; }

    public string? Instance { get; init; }

    public List<SubproblemWire>? Subproblems { get; init; }

    // A problem document is already an error report: an incomplete part of it is dropped, never thrown over.
    public AcmeProblem ToModel() => new(
        Type ?? "about:blank",
        Detail,
        Status,
        [.. (Subproblems ?? []).Select(static s => new AcmeSubproblem(s.Type ?? "about:blank", s.Detail, s.Identifier is { IsComplete: true } ? s.Identifier : null))],
        Instance);
}

internal sealed class SubproblemWire
{
    public string? Type { get; init; }

    public string? Detail { get; init; }

    public AcmeIdentifier? Identifier { get; init; }
}

internal sealed class RenewalInfoWire
{
    public SuggestedWindowWire? SuggestedWindow { get; init; }

    [JsonPropertyName("explanationURL")]
    public Uri? ExplanationUrl { get; init; }
}

internal sealed class SuggestedWindowWire
{
    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }
}

internal sealed class NewAccountWire
{
    public List<string>? Contact { get; init; }

    public bool? TermsOfServiceAgreed { get; init; }

    public bool? OnlyReturnExisting { get; init; }

    public JwsFlattened? ExternalAccountBinding { get; init; }
}

internal sealed class ContactUpdateWire
{
    public required List<string> Contact { get; init; }
}

internal sealed class DeactivateWire
{
    public string Status { get; init; } = "deactivated";
}

internal sealed class NewOrderWire
{
    public required List<AcmeIdentifier> Identifiers { get; init; }

    public string? Profile { get; init; }

    public string? Replaces { get; init; }
}

internal sealed class FinalizeWire
{
    public required string Csr { get; init; }
}

internal sealed class RevokeWire
{
    public required string Certificate { get; init; }

    public int? Reason { get; init; }
}
