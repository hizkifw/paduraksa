using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Data.Conversion;

// The model's enums are stored by their stable names (ModelNames), never by number: the names are what the audit
// chain hashes, and they keep the columns readable. Applied to every property of these types by convention.

internal sealed class ActorKindConverter()
    : ValueConverter<ActorKind, string>(value => value.ToName(), name => ModelNames.ParseActorKind(name));

internal sealed class ChannelConverter()
    : ValueConverter<Channel, string>(value => value.ToName(), name => ModelNames.ParseChannel(name));

internal sealed class CertificateSourceConverter()
    : ValueConverter<CertificateSource, string>(value => value.ToName(), name => ModelNames.ParseCertificateSource(name));

internal sealed class CertificateStatusConverter()
    : ValueConverter<CertificateStatus, string>(value => value.ToName(), name => ModelNames.ParseCertificateStatus(name));

internal sealed class AuditOutcomeConverter()
    : ValueConverter<AuditOutcome, string>(value => value.ToName(), name => ModelNames.ParseAuditOutcome(name));

internal sealed class AcmeAccountStatusConverter()
    : ValueConverter<AcmeAccountStatus, string>(value => value.ToName(), name => ModelNames.ParseAcmeAccountStatus(name));

internal sealed class AcmeOrderStatusConverter()
    : ValueConverter<AcmeOrderStatus, string>(value => value.ToName(), name => ModelNames.ParseAcmeOrderStatus(name));

internal sealed class AcmeChallengeTypeConverter()
    : ValueConverter<AcmeChallengeType, string>(value => value.ToName(), name => ModelNames.ParseAcmeChallengeType(name));

internal sealed class DnsProviderKindConverter()
    : ValueConverter<DnsProviderKind, string>(value => value.ToName(), name => ModelNames.ParseDnsProviderKind(name));

internal sealed class JwsAlgorithmConverter()
    : ValueConverter<JwsAlgorithm, string>(value => value.ToName(), name => JwsAlgorithmNames.Parse(name));

internal sealed class KeySpecConverter()
    : ValueConverter<KeySpec, string>(value => value.ToName(), name => VaultNames.ParseKeySpec(name));
