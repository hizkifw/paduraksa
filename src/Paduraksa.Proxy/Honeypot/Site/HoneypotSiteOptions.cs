namespace Paduraksa.Proxy.Honeypot;

/// <summary>What a host's trap site is told besides its key.</summary>
/// <param name="Prefix">The trap prefix, canonical, starting and ending with <c>/</c>.</param>
/// <param name="SiteName">The blog's title. Any text: it is HTML-escaped once, when the site is built.</param>
/// <param name="PowShare">Percent of the challengeable pages (posts, categories, tags, authors) behind the proof-of-work challenge, 0 to 100.</param>
public sealed record HoneypotSiteOptions(string Prefix, string SiteName, int PowShare);
