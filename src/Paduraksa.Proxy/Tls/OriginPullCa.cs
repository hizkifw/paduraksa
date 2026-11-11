using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// The certificate authority behind Cloudflare's Authenticated Origin Pulls: it signs the client certificate Cloudflare's
/// edge presents to an origin. The shipped copy is Cloudflare's zone-level one, an embedded resource
/// (<c>cloudflare-origin-pull-ca.pem</c>, which records its source URL, fetch date and expiry in its header comment).
/// </summary>
/// <remarks>
/// Public data, so a build ships it; it expires on 2029-11-01, after which Cloudflare will have published a successor and a
/// build with the new file is needed (the compiler's <c>OriginPullCa</c> option takes a replacement without one).
/// </remarks>
public static class OriginPullCa
{
    private const string ResourceName = "Paduraksa.Proxy.Tls.cloudflare-origin-pull-ca.pem";
    private const string Begin = "-----BEGIN CERTIFICATE-----";
    private const string End = "-----END CERTIFICATE-----";

    private static readonly Lazy<X509Certificate2Collection> Shipped = new(Load);

    /// <summary>Cloudflare's origin-pull CA as shipped. Loaded once; never dispose its certificates.</summary>
    public static X509Certificate2Collection Default => Shipped.Value;

    private static X509Certificate2Collection Load()
    {
        using var stream = typeof(OriginPullCa).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using StreamReader reader = new(stream, System.Text.Encoding.ASCII);
        return FromPem(reader.ReadToEnd());
    }

    /// <summary>The certificates of a PEM text (anything outside the <c>BEGIN</c>/<c>END</c> lines is ignored, comments
    /// included). For a replacement the operator supplies; public data only.</summary>
    /// <exception cref="FormatException">The text holds no certificate, or one that isn't valid.</exception>
    public static X509Certificate2Collection FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var text = pem;
        X509Certificate2Collection roots = [];
        var from = 0;
        while (text.IndexOf(Begin, from, StringComparison.Ordinal) is var start and >= 0)
        {
            var body = start + Begin.Length;
            var end = text.IndexOf(End, body, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            try
            {
                roots.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text.AsSpan(body, end - body).ToString())));
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                throw new FormatException("A certificate in the text isn't valid.", ex);
            }

            from = end + End.Length;
        }

        return roots.Count > 0 ? roots : throw new FormatException("The text holds no certificate.");
    }
}
