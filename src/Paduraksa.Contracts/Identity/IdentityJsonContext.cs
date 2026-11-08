using System.Text.Json;
using System.Text.Json.Serialization;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Identity;

/// <summary>
/// The source-generated JSON contract of the identity DTOs: camelCase properties, nulls omitted, typed IDs as their string form,
/// roles and permissions as their stable names (<c>owner</c>, <c>config:write</c>). Reading is strict: unknown properties and
/// missing required members are errors.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(TypedIdJsonConverterFactory), typeof(RoleJsonConverter), typeof(PermissionJsonConverter)])]
[JsonSerializable(typeof(UserSummary))]
[JsonSerializable(typeof(IReadOnlyList<UserSummary>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(SecondFactorRequest))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(IReadOnlyList<SessionSummary>))]
[JsonSerializable(typeof(ApiTokenSummary))]
[JsonSerializable(typeof(IReadOnlyList<ApiTokenSummary>))]
[JsonSerializable(typeof(IssueApiTokenRequest))]
[JsonSerializable(typeof(IssuedApiToken))]
[JsonSerializable(typeof(TotpEnrollmentStarted))]
[JsonSerializable(typeof(RecoveryCodes))]
[JsonSerializable(typeof(SetupStatus))]
public sealed partial class IdentityJsonContext : JsonSerializerContext;

internal sealed class RoleJsonConverter : JsonConverter<Role>
{
    public override Role Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name)
        {
            try
            {
                return ModelNames.ParseRole(name);
            }
            catch (FormatException)
            {
                // Falls through to the JsonException below.
            }
        }

        throw new JsonException("Expected a role name: owner, admin, operator, viewer or auditor.");
    }

    public override void Write(Utf8JsonWriter writer, Role value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToName());
    }
}

internal sealed class PermissionJsonConverter : JsonConverter<Permission>
{
    public override Permission Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && ModelNames.TryParsePermission(reader.GetString(), out var permission))
        {
            return permission;
        }

        throw new JsonException("Expected a permission name such as config:read.");
    }

    public override void Write(Utf8JsonWriter writer, Permission value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToName());
    }
}
