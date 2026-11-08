using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Acme;

/// <summary>Tunables of <see cref="AcmeClient"/>. The defaults suit a public CA. A record, so a variation is <c>options with { ... }</c>.</summary>
public sealed record AcmeClientOptions
{
    /// <summary>The longest one HTTP exchange may take, headers and body. Applied per request, on the client's <see cref="TimeProvider"/>.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a request refused with <c>badNonce</c> is signed again with a fresh nonce (RFC 8555 §6.5) before the problem is thrown.</summary>
    public int MaxBadNonceRetries { get; init; } = 3;

    /// <summary>The delay between polls when the CA sent no <c>Retry-After</c>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The shortest delay between polls: a <c>Retry-After: 0</c> does not make the client spin.</summary>
    public TimeSpan MinPollDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between polls: a larger <c>Retry-After</c> is shortened to this, so a polling call stays bounded.</summary>
    public TimeSpan MaxPollDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a <c>WaitFor...</c> call polls in all before it gives up with an <see cref="AcmeTransportException"/>.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a fetched directory is reused.</summary>
    public TimeSpan DirectoryCacheDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The largest response body accepted, in bytes. A directory, an order and a certificate chain are a few kilobytes.</summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>The <c>User-Agent</c> product token (RFC 8555 §6.1 asks for one).</summary>
    public string UserAgent { get; init; } = "Paduraksa-Acme/1.0";

    /// <summary>
    /// Origins (scheme, host, port) besides the directory's that the CA may use. By default every URL the client requests or
    /// accepts from the CA (order and challenge URLs, <c>Location</c>, alternate chains, <c>renewalInfo</c>) must have the
    /// directory's origin, so a hostile or hijacked answer cannot make the client sign requests for another host. A CA that
    /// really serves from several hosts lists the extra ones here (only scheme, host and port are used).
    /// </summary>
    public IReadOnlyList<Uri> AdditionalOrigins { get; init; } = [];

    /// <summary>
    /// Accept <c>http://</c> URLs <b>of loopback hosts</b> (<c>localhost</c>, 127.0.0.0/8, <c>::1</c>): a CA test server on this machine.
    /// Off, and meant to stay off outside tests: ACME runs over HTTPS (RFC 8555 §6.1), and every URL the CA hands out is checked
    /// against it. Even when on, an <c>http://</c> URL of any other host is refused, so a hijacked answer cannot move the client onto
    /// the network in the clear.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }
}

/// <summary>The HTTP handler an <see cref="AcmeClient"/> should run on.</summary>
public static class AcmeHttp
{
    /// <summary>
    /// The handler <see cref="AcmeClient"/> uses unless it is given one: it never follows redirects. A signed ACME request is
    /// bound to its URL (RFC 8555 §6.4), so a redirect would resend a body signed for another URL to a new location, where it
    /// is useless to the CA and visible to whoever answers. The client rejects a redirect it can see, either as a 3xx or as a
    /// response whose request URL differs; this handler makes sure it sees the 3xx. A handler passed in must not follow
    /// redirects and must not retry: a retry resends a signed body with a nonce the CA has already used.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    };

    /// <summary>
    /// <see cref="CreateHandler()"/> that also trusts <paramref name="additionalRoots"/> for the CA's TLS certificate, for a private CA
    /// (step-ca, Pebble) or an interception root. Only the <i>chain</i> check is widened: a chain the system trusts is accepted as
    /// always; otherwise the chain (the certificate and the intermediates the server sent) must be valid now, end at one of these
    /// roots and allow server authentication (the <c>serverAuth</c> extended key usage), and the name must match as ever. Revocation is
    /// not checked for either kind (the transport's default). Each root must be a self-signed CA certificate: an intermediate or
    /// a leaf is not a root and is refused, so the trust cannot be narrowed to (or silently widened by) a certificate that is not an anchor.
    /// </summary>
    /// <param name="additionalRoots">The roots to trust besides the system's. The handler keeps copies; the caller may dispose these.</param>
    /// <exception cref="ArgumentException">A certificate is not a self-signed CA certificate.</exception>
    public static SocketsHttpHandler CreateHandler(IReadOnlyCollection<X509Certificate2> additionalRoots)
    {
        ArgumentNullException.ThrowIfNull(additionalRoots);
        var handler = CreateHandler();
        if (additionalRoots.Count == 0)
        {
            return handler;
        }

        var roots = new X509Certificate2Collection();
        foreach (var root in additionalRoots)
        {
            if (!IsSelfSignedCa(root))
            {
                throw new ArgumentException($"{root.SubjectName.Name} is not a self-signed CA certificate; a trusted root must be one.", nameof(additionalRoots));
            }

            roots.Add(X509CertificateLoader.LoadCertificate(root.RawData));
        }

        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) => TrustedWith(roots, certificate, chain, errors);
        return handler;
    }

    /// <summary>Whether <paramref name="certificate"/> is a CA that names itself as its issuer (its signature is checked when it is used as an anchor).</summary>
    public static bool IsSelfSignedCa(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        return constraints is { CertificateAuthority: true } && certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
    }

    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    private static bool TrustedWith(X509Certificate2Collection roots, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // Only a chain the system does not trust is retried; a missing certificate or a name mismatch is final.
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null || chain is null)
        {
            return false;
        }

        using var verify = new X509Chain();
        verify.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        verify.ChainPolicy.CustomTrustStore.AddRange(roots);
        verify.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        verify.ChainPolicy.ApplicationPolicy.Add(ServerAuthentication);

        // What the server sent beyond the leaf is all there is to build the path from: the policy's extra store and the chain the platform built.
        verify.ChainPolicy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
        foreach (var element in chain.ChainElements)
        {
            verify.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        using var leaf = new X509Certificate2(certificate);
        return verify.Build(leaf);
    }
}
