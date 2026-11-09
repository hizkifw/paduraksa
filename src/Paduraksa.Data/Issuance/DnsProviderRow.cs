using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Issuance;

/// <summary>
/// A configured DNS provider for DNS-01 challenges. <b>No credential:</b> the API token is a vault secret
/// (<see cref="CredentialSecretId"/>), and the hosts it may be sent to are stored with that secret, not here.
/// </summary>
internal sealed class DnsProviderRow
{
    public DnsProviderId Id { get; set; }

    public DnsProviderKind Kind { get; set; }

    public string Label { get; set; } = "";

    /// <summary>The vault secret holding the provider's credential (<c>SecretKind.DnsApiToken</c>).</summary>
    public SecretId CredentialSecretId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class DnsProviderRowConfiguration : IEntityTypeConfiguration<DnsProviderRow>
{
    public void Configure(EntityTypeBuilder<DnsProviderRow> builder)
    {
        builder.ToTable("DnsProviders", table => table.HasCheckConstraint("CK_DnsProviders_Kind", NameChecks.DnsProviderKind("Kind")));
    }
}
