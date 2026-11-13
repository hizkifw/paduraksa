namespace Paduraksa.Acme.Dns;

/// <summary>
/// Where a DNS-01 TXT record really goes. A zone owner who keeps challenge tokens out of the main zone CNAMEs
/// <c>_acme-challenge.name</c> to a name in another zone, and the CA follows that CNAME when it validates: the record must be
/// published, checked and removed at the end of the chain.
/// </summary>
public interface IDnsChallengeNames
{
    /// <summary>
    /// The name to publish at for <paramref name="recordName"/>: the end of its CNAME chain, or <paramref name="recordName"/> itself when it
    /// is not a CNAME or the resolvers cannot say (the provider then meets the CNAME and refuses it with its own message). Never throws
    /// for DNS trouble.
    /// </summary>
    /// <exception cref="DnsProviderException">The chain loops or is longer than the CA would follow: no TXT record can make it validate.</exception>
    ValueTask<string> ResolveAsync(string recordName, CancellationToken cancellationToken);
}
