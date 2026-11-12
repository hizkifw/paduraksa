using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Admin.Json;

/// <summary>The names an enum has on the wire, for the API description.</summary>
internal static class WireEnums
{
    private static readonly FrozenDictionary<Type, IReadOnlyList<string>> ModelEnums = new Dictionary<Type, IReadOnlyList<string>>
    {
        [typeof(ActorKind)] = Names<ActorKind>(ModelNames.ToName),
        [typeof(Channel)] = Names<Channel>(ModelNames.ToName),
        [typeof(AuditOutcome)] = Names<AuditOutcome>(ModelNames.ToName),
        [typeof(CertificateSource)] = Names<CertificateSource>(ModelNames.ToName),
        [typeof(CertificateStatus)] = Names<CertificateStatus>(ModelNames.ToName),
        [typeof(AcmeAccountStatus)] = Names<AcmeAccountStatus>(ModelNames.ToName),
        [typeof(AcmeOrderStatus)] = Names<AcmeOrderStatus>(ModelNames.ToName),
        [typeof(AcmeChallengeType)] = Names<AcmeChallengeType>(ModelNames.ToName),
        [typeof(DnsProviderKind)] = Names<DnsProviderKind>(ModelNames.ToName),
        [typeof(NotificationChannelKind)] = Names<NotificationChannelKind>(ModelNames.ToName),
        [typeof(SmtpSecurity)] = Names<SmtpSecurity>(ModelNames.ToName),
        [typeof(NotificationEventKind)] = Names<NotificationEventKind>(ModelNames.ToName),
        [typeof(NotificationDeliveryState)] = Names<NotificationDeliveryState>(ModelNames.ToName),
        [typeof(KeySpec)] = Names<KeySpec>(VaultNames.ToName),
        [typeof(JwsAlgorithm)] = Names<JwsAlgorithm>(JwsAlgorithmNames.ToName),
    }.ToFrozenDictionary();

    /// <summary>
    /// The wire names of <paramref name="enumType"/>: the model's stable names for the enums of the model and vault, the camelCase names for the
    /// config enums (which say so with a converter attribute); null for an enum the API doesn't serialize.
    /// </summary>
    public static IReadOnlyList<string>? NamesOf(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        if (ModelEnums.TryGetValue(enumType, out var names))
        {
            return names;
        }

        if (Attribute.IsDefined(enumType, typeof(JsonConverterAttribute), inherit: false))
        {
            return [.. Enum.GetNames(enumType).Select(n => JsonNamingPolicy.CamelCase.ConvertName(n))];
        }

        return null;
    }

    private static string[] Names<TEnum>(Func<TEnum, string> toName)
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(toName)];
}
