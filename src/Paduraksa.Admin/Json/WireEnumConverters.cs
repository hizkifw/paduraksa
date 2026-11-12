using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Admin.Json;

/// <summary>
/// Writes an enum of the model as its stable wire name (see <see cref="ModelNames"/>): the
/// name the database and the audit log use, so every surface says the same word. Reading accepts exactly those names; a number, another
/// casing or an unknown name is a <see cref="JsonException"/> (a 400 for the caller).
/// </summary>
internal abstract class WireEnumConverter<TEnum>(Func<TEnum, string> toName) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly FrozenDictionary<string, TEnum> _byName = Enum.GetValues<TEnum>().ToFrozenDictionary(toName, StringComparer.Ordinal);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && _byName.TryGetValue(name, out var value))
        {
            return value;
        }

        throw new JsonException($"Expected one of {string.Join(", ", _byName.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))} for {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Enum.IsDefined(value) ? toName(value) : throw new JsonException($"Undefined {typeof(TEnum).Name}."));
    }
}

internal sealed class ActorKindWireConverter() : WireEnumConverter<ActorKind>(ModelNames.ToName);

internal sealed class ChannelWireConverter() : WireEnumConverter<Channel>(ModelNames.ToName);

internal sealed class AuditOutcomeWireConverter() : WireEnumConverter<AuditOutcome>(ModelNames.ToName);

internal sealed class CertificateSourceWireConverter() : WireEnumConverter<CertificateSource>(ModelNames.ToName);

internal sealed class CertificateStatusWireConverter() : WireEnumConverter<CertificateStatus>(ModelNames.ToName);

internal sealed class AcmeAccountStatusWireConverter() : WireEnumConverter<AcmeAccountStatus>(ModelNames.ToName);

internal sealed class AcmeOrderStatusWireConverter() : WireEnumConverter<AcmeOrderStatus>(ModelNames.ToName);

internal sealed class AcmeChallengeTypeWireConverter() : WireEnumConverter<AcmeChallengeType>(ModelNames.ToName);

internal sealed class DnsProviderKindWireConverter() : WireEnumConverter<DnsProviderKind>(ModelNames.ToName);

internal sealed class NotificationChannelKindWireConverter() : WireEnumConverter<NotificationChannelKind>(ModelNames.ToName);

internal sealed class SmtpSecurityWireConverter() : WireEnumConverter<SmtpSecurity>(ModelNames.ToName);

internal sealed class NotificationEventKindWireConverter() : WireEnumConverter<NotificationEventKind>(ModelNames.ToName);

internal sealed class NotificationDeliveryStateWireConverter() : WireEnumConverter<NotificationDeliveryState>(ModelNames.ToName);

internal sealed class KeySpecWireConverter() : WireEnumConverter<KeySpec>(VaultNames.ToName);

internal sealed class JwsAlgorithmWireConverter() : WireEnumConverter<JwsAlgorithm>(JwsAlgorithmNames.ToName);
