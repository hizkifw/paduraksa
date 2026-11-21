namespace Paduraksa.Proxy.Honeypot;

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
    public string DerivePrefix(ReadOnlySpan<byte> siteKey) => HoneypotSite.DerivePrefix(siteKey);

    public string DeriveSiteName(ReadOnlySpan<byte> siteKey) => HoneypotSite.DeriveSiteName(siteKey);

    public IHoneypotSite Create(ReadOnlySpan<byte> siteKey, HoneypotSiteOptions options) => new HoneypotSite(siteKey, options);
}
