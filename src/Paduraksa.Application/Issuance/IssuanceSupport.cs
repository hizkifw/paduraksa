using System.Text;
using System.Text.Json;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Data.Issuance;

namespace Paduraksa.Application.Issuance;

/// <summary>The stable <see cref="Paduraksa.Contracts.Config.ConfigError.Code"/>s of the issuance services.</summary>
public static class IssuanceErrorCodes
{
    public const string DirectoryInvalid = "acme.account.directory.invalid";
    public const string ContactInvalid = "acme.account.contact.invalid";
    public const string EabIncomplete = "acme.account.eab.incomplete";
    public const string EabSecretMissing = "acme.account.eab.secretMissing";
    public const string AlgorithmUndefined = "acme.account.algorithm.undefined";
    public const string AccountInUse = "acme.account.inUse";
    public const string AccountInactive = "acme.account.inactive";
    public const string AccountUnknown = "acme.account.unknown";
    public const string TextTooLong = "acme.text.tooLong";
    public const string LabelRequired = "acme.dnsProvider.label.required";
    public const string KindUndefined = "acme.dnsProvider.kind.undefined";
    public const string CredentialMissing = "acme.dnsProvider.credential.missing";
    public const string DnsProviderInUse = "acme.dnsProvider.inUse";
    public const string DnsProviderUnknown = "acme.dnsProvider.unknown";
    public const string DnsProviderRequired = "acme.order.dnsProvider.required";
    public const string DnsProviderNotAllowed = "acme.order.dnsProvider.notAllowed";
    public const string NamesRequired = "acme.order.names.required";
    public const string NameInvalid = "acme.order.name.invalid";
    public const string TooManyNames = "acme.order.names.tooMany";
    public const string WildcardNeedsDns = "acme.order.name.wildcardNeedsDns01";
    public const string IpNeedsHttp = "acme.order.name.ipNeedsHttp01";
    public const string ChallengeUndefined = "acme.order.challenge.undefined";
    public const string KeySpecUndefined = "acme.order.keySpec.undefined";
}

/// <summary>Row-to-DTO mapping and small helpers shared by the issuance services.</summary>
internal static class IssuanceMapping
{
    public static AcmeAccountSummary ToSummary(AcmeAccountRow row) => new(
        row.Id,
        row.DirectoryUrl,
        [.. row.Contacts],
        row.Status,
        row.AccountUrl,
        row.KeyAlgorithm,
        row.EabKid,
        row.EabSecretId,
        row.TermsOfServiceAgreed,
        row.Label,
        row.CreatedAt,
        row.UpdatedAt);

    public static DnsProviderSummary ToSummary(DnsProviderRow row) => new(row.Id, row.Kind, row.Label, row.CredentialSecretId, row.CreatedAt, row.UpdatedAt);

    public static AcmeOrderSummary ToSummary(AcmeOrderRow row) => new(
        row.CertificateId,
        row.Id,
        row.AccountId,
        row.Status,
        row.ChallengeType,
        row.DnsProviderId,
        row.KeySpec,
        [.. row.Names],
        row.Profile,
        row.PreferredChain,
        row.Error,
        row.Attempts,
        row.LastAttemptAt,
        row.RetryAt,
        row.NextRenewalAt,
        row.AriWindowStart,
        row.AriWindowEnd,
        row.AriExplanationUrl,
        row.AriCheckAt,
        row.CreatedAt,
        row.UpdatedAt);

    public static AuditEntry Entry<TId>(CommandOrigin origin, string action, TId id, AuditOutcome outcome)
        where TId : struct, ITypedId<TId> =>
        new(origin.Actor, origin.Channel, action, outcome) { Target = id.ToString(), ClientIp = origin.ClientIp };

    public static string Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Text that came from outside (a CA's detail, a provider's message) as one printable line of at most <paramref name="max"/> characters.</summary>
    public static string Sanitize(string? text, int max = 512)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder line = new(Math.Min(text.Length, max));
        foreach (char c in text)
        {
            if (line.Length >= max)
            {
                break;
            }

            line.Append(char.IsControl(c) ? ' ' : c);
        }

        return line.ToString().Trim();
    }
}
