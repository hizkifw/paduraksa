using System.Text.Json.Serialization;

using Paduraksa.Core.Ids;

namespace Paduraksa.Contracts.Simulation;

/// <summary>
/// The source-generated JSON contract of the simulator (<c>POST /api/v1/simulate</c>, MCP <c>simulate_request</c>): same
/// conventions as the config document (<c>ConfigJsonContext</c>): camelCase, nulls omitted, enums as camelCase names, typed
/// IDs as their string form, and strict reading. A target names its kind in <c>kind</c>; a draft command names its own in
/// <c>command</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(TypedIdJsonConverterFactory)])]
[JsonSerializable(typeof(SimulationRequest))]
[JsonSerializable(typeof(SimulationTarget))]
[JsonSerializable(typeof(SimulationResult))]
public sealed partial class SimulationJsonContext : JsonSerializerContext;
