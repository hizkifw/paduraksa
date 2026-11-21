namespace Paduraksa.Proxy.Honeypot;

// PLACEHOLDER, replaced at the merge with honeypot-generator: that branch defines HoneypotSiteOptions (the same shape) and
// HoneypotSite, so this record is deleted and the default factory below calls HoneypotSite.DerivePrefix, DeriveSiteName and its
// constructor.
/// <summary>What a trap site is built from besides its key.</summary>
/// <param name="Prefix">The trap prefix.</param>
/// <param name="SiteName">The blog's title.</param>
/// <param name="PowShare">Percent of pages behind a proof-of-work challenge, 0 to 100.</param>
public sealed record HoneypotSiteOptions(string Prefix, string SiteName, int PowShare);

/// <summary>
/// The one seam the compiler builds a host's trap site through, so what the pages look like is the generator's business and the
/// gate's tests can use a small fake.
/// </summary>
public interface IHoneypotSiteFactory
{
    /// <summary>The prefix a host without a configured path gets.</summary>
    string DerivePrefix(ReadOnlySpan<byte> siteKey);

    /// <summary>The title a host without a configured name gets.</summary>
    string DeriveSiteName(ReadOnlySpan<byte> siteKey);

    /// <summary>The site for one host; <paramref name="siteKey"/> is 32 bytes.</summary>
    IHoneypotSite Create(ReadOnlySpan<byte> siteKey, HoneypotSiteOptions options);
}

/// <summary>The factory a process uses unless it is given another.</summary>
internal sealed class DefaultHoneypotSiteFactory : IHoneypotSiteFactory
{
    // PLACEHOLDER: the merge makes each member one line over HoneypotSite.
    public string DerivePrefix(ReadOnlySpan<byte> siteKey) => throw NotWired();

    public string DeriveSiteName(ReadOnlySpan<byte> siteKey) => throw NotWired();

    public IHoneypotSite Create(ReadOnlySpan<byte> siteKey, HoneypotSiteOptions options) => throw NotWired();

    private static NotSupportedException NotWired() => new("This build has no honeypot site generator.");
}
