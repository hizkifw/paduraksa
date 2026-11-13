using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Identity;

/// <summary>
/// The source-generated JSON contract of the identity DTOs: nulls written (a response always has every member, and a
/// nullable member without a default is required on read), typed IDs as their string form, roles and permissions as
/// their stable names (<c>owner</c>, <c>config:write</c>). Reading is strict: unknown properties and missing required
/// members are errors.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(TypedIdJsonConverterFactory), typeof(RoleJsonConverter), typeof(PermissionJsonConverter)])]
[JsonSerializable(typeof(UserSummary))]
[JsonSerializable(typeof(Paduraksa.Contracts.Api.Page<UserSummary>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(SecondFactorRequest))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(Paduraksa.Contracts.Api.Page<SessionSummary>))]
[JsonSerializable(typeof(ApiTokenSummary))]
[JsonSerializable(typeof(Paduraksa.Contracts.Api.Page<ApiTokenSummary>))]
[JsonSerializable(typeof(IssueApiTokenRequest))]
[JsonSerializable(typeof(IssuedApiToken))]
[JsonSerializable(typeof(TotpEnrollmentStarted))]
[JsonSerializable(typeof(RecoveryCodes))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(SessionStarted))]
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(SetupCompleted))]
[JsonSerializable(typeof(PasswordConfirmation))]
[JsonSerializable(typeof(TotpConfirmation))]
[JsonSerializable(typeof(CurrentCaller))]
public sealed partial class IdentityJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Makes <paramref name="options"/> (the admin app's HTTP JSON options) read and write the identity DTOs the way this context does. The
    /// context is put first in the resolver chain, and its converters are added, because a context's own converters apply only when it is
    /// used as the serializer itself, not as one resolver among several. Calling it again changes nothing.
    /// </summary>
    /// <remarks>
    /// Reading is strict, which the context's own options would not carry over: unknown properties are refused (for these types), a
    /// constructor parameter without a default must be present, null is refused where the type does not allow it, and a property given
    /// twice is an error. The last three are <i>options-wide</i>: they apply to every type the options serialize.
    /// </remarks>
    public static void AddTo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.AllowDuplicateProperties = false;
        if (!options.Converters.Any(static c => c is TypedIdJsonConverterFactory))
        {
            options.Converters.Add(new TypedIdJsonConverterFactory());
        }

        if (!options.Converters.Any(static c => c is RoleJsonConverter))
        {
            options.Converters.Add(new RoleJsonConverter());
        }

        if (!options.Converters.Any(static c => c is PermissionJsonConverter))
        {
            options.Converters.Add(new PermissionJsonConverter());
        }

        if (!options.TypeInfoResolverChain.Any(static r => r is StrictIdentityResolver))
        {
            options.TypeInfoResolverChain.Insert(0, new StrictIdentityResolver());
        }
    }

    private sealed class StrictIdentityResolver : IJsonTypeInfoResolver
    {
        private static readonly IJsonTypeInfoResolver Inner = ((IJsonTypeInfoResolver)Default).WithAddedModifier(static info =>
        {
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                info.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            }
        });

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => Inner.GetTypeInfo(type, options);
    }
}

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
