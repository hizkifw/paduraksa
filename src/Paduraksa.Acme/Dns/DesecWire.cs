using System.Text.Json.Serialization;

namespace Paduraksa.Acme.Dns;

// The JSON shapes of the deSEC API that the provider reads and writes. Listed here and source-generated; nothing is
// serialized by reflection.

/// <summary>The source-generated JSON context for the deSEC API. Names are snake_case, as deSEC spells them.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(List<DesecDomain>))]
[JsonSerializable(typeof(DesecRrset))]
[JsonSerializable(typeof(DesecRrsetWrite))]
[JsonSerializable(typeof(List<DesecRrsetWrite>))]
internal sealed partial class DesecJsonContext : JsonSerializerContext;

/// <summary>A domain of the account (only what the provider uses).</summary>
internal sealed class DesecDomain
{
    public string? Name { get; init; }

    /// <summary>The lowest TTL an RRset in this domain may have (3600 unless the account was granted lower).</summary>
    public int? MinimumTtl { get; init; }
}

/// <summary>An RRset as deSEC returns it; <c>records</c> are in presentation form (a TXT value is quoted).</summary>
internal sealed class DesecRrset
{
    public int Ttl { get; init; }

    public List<string>? Records { get; init; }
}

/// <summary>One RRset of a bulk write: <c>subname</c> and <c>type</c> name it, <c>ttl</c> and the whole <c>records</c> list (empty deletes the set) say what it becomes.</summary>
internal sealed class DesecRrsetWrite
{
    public string? Subname { get; init; }

    public string? Type { get; init; }

    public int Ttl { get; init; }

    public required List<string> Records { get; init; }
}
