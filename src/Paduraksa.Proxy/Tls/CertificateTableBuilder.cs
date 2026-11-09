using System.Collections.Frozen;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Tls;

/// <summary>Builds a <see cref="CertificateTable"/> (cold path, snapshot compile time).</summary>
/// <remarks>
/// IDNs are configured in punycode (<c>xn--bcher-kva.example</c>). SslStream hands the TCP callback the SNI decoded to
/// Unicode (<c>bücher.example</c>) while QUIC passes punycode through, so such names are indexed in both forms (exact and
/// wildcard). The lookup itself stays a single 0 B dictionary probe.
/// </remarks>
public sealed class CertificateTableBuilder
{
    private readonly Dictionary<string, CertificateTableEntry> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CertificateTableEntry> _wildcard = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CertificateTableEntry> _exactUnicode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CertificateTableEntry> _wildcardUnicode = new(StringComparer.Ordinal);
    private readonly Dictionary<(SslStreamCertificateContext Context, TlsPolicy Policy), CertificateTableEntry> _entries = [];
    private readonly List<CertificateTableEntry> _entryOrder = [];
    private CertificateTableEntry? _default;

    /// <summary>Adds one host: its names (exact or <c>*.parent</c>), its certificate and its TLS policy.</summary>
    /// <exception cref="ArgumentException">A name is invalid, or already configured (by this or an earlier host).</exception>
    public CertificateTableBuilder Add(IEnumerable<string> names, ServerCertificate certificate, TlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(policy);

        // Validate everything first so a bad host can't leave half its names behind.
        var normalized = new List<(string Key, bool Wildcard, string? Unicode)>();
        var seen = new HashSet<(string, bool)>();
        foreach (var name in names)
        {
            var key = ServerNames.Normalize(name, out var wildcard, out var unicode);
            var table = wildcard ? _wildcard : _exact;
            if (table.ContainsKey(key) || !seen.Add((key, wildcard)))
            {
                throw new ArgumentException(
                    $"The server name '{name}' is configured more than once (names are case-insensitive and ignore a trailing dot). Each name must belong to exactly one host.",
                    nameof(names));
            }

            normalized.Add((key, wildcard, unicode));
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("A host needs at least one server name.", nameof(names));
        }

        var entry = GetOrCreateEntry(certificate, policy);
        foreach (var (key, wildcard, unicode) in normalized)
        {
            (wildcard ? _wildcard : _exact).Add(key, entry);
            if (unicode is not null)
            {
                // Distinct punycode names decode to distinct Unicode names, and configured names are ASCII-only, so
                // an alias can't collide with anything else.
                (wildcard ? _wildcardUnicode : _exactUnicode).Add(unicode, entry);
            }
        }

        return this;
    }

    /// <summary>Sets the certificate served for unknown or absent SNI. Without a default those are rejected.</summary>
    public CertificateTableBuilder SetDefault(ServerCertificate certificate, TlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(policy);
        _default = GetOrCreateEntry(certificate, policy);
        return this;
    }

    public CertificateTable Build()
    {
        // A replaced default may no longer be referenced by anything: leave such entries out.
        var referenced = new HashSet<CertificateTableEntry>(_exact.Values, ReferenceEqualityComparer.Instance);
        referenced.UnionWith(_wildcard.Values);
        if (_default is not null)
        {
            referenced.Add(_default);
        }

        var entries = new List<CertificateTableEntry>(_entryOrder.Count);
        var options = new List<SslServerAuthenticationOptions>(_entryOrder.Count * 2);
        foreach (var entry in _entryOrder)
        {
            if (!referenced.Contains(entry))
            {
                continue;
            }

            entries.Add(entry);
            options.Add(entry.TcpOptions);
            if (entry.QuicOptions is not null)
            {
                options.Add(entry.QuicOptions);
            }
        }

        return new CertificateTable(
            WithAliases(_exact, _exactUnicode),
            WithAliases(_wildcard, _wildcardUnicode),
            KeysOf(_exact),
            KeysOf(_wildcard),
            _default,
            [.. entries],
            [.. options]);
    }

    // Not a collection-expression spread: over a KeyCollection that compiles to Enumerable.ToArray (no LINQ in Proxy).
    private static string[] KeysOf(Dictionary<string, CertificateTableEntry> names)
    {
        var keys = new string[names.Count];
        names.Keys.CopyTo(keys, 0);
        return keys;
    }

    private static FrozenDictionary<string, CertificateTableEntry> WithAliases(
        Dictionary<string, CertificateTableEntry> names,
        Dictionary<string, CertificateTableEntry> unicodeAliases)
    {
        var all = new Dictionary<string, CertificateTableEntry>(names, StringComparer.Ordinal);
        foreach (var (alias, entry) in unicodeAliases)
        {
            all.Add(alias, entry);
        }

        return all.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private CertificateTableEntry GetOrCreateEntry(ServerCertificate certificate, TlsPolicy policy)
    {
        // Deduplicated on (certificate context, policy); the transport split is inside the entry.
        var key = (certificate.Context, policy);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new CertificateTableEntry(
                certificate,
                policy,
                CreateTcpOptions(certificate, policy),
                policy.Http3Enabled ? CreateQuicOptions(certificate, policy) : null);
            _entries.Add(key, entry);
            _entryOrder.Add(entry);
        }

        return entry;
    }

    /// <summary>
    /// Cipher suites for TCP: AEAD only, ECDHE only (forward secrecy). TLS 1.3 suites are all AEAD; for
    /// TLS 1.2 this drops the CBC/SHA-1 and static-RSA suites OpenSSL's default list still allows. Linux only:
    /// <see cref="CipherSuitesPolicy"/> isn't supported on Windows, and Linux is what we ship, so other platforms keep
    /// the OS defaults. QUIC doesn't get it: QUIC is TLS 1.3, whose suites are all AEAD already.
    /// </summary>
    private static readonly CipherSuitesPolicy? TcpCipherSuites = OperatingSystem.IsLinux()
        ? new CipherSuitesPolicy(
        [
            TlsCipherSuite.TLS_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        ])
        : null;

    // ApplicationProtocols is ALWAYS set: both Kestrel transports write their own ALPN into a returned options object
    // whose list is null, which breaks the other transport when instances are shared.
    private static SslServerAuthenticationOptions CreateTcpOptions(ServerCertificate certificate, TlsPolicy policy) => new()
    {
        CipherSuitesPolicy = TcpCipherSuites,
        ServerCertificateContext = certificate.Context,
        ApplicationProtocols = policy.TcpAlpn == TcpAlpn.Http11Only
            ? [SslApplicationProtocol.Http11]
            : [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
        EnabledSslProtocols = policy.MinimumVersion == TlsMinimumVersion.Tls13
            ? SslProtocols.Tls13
            : SslProtocols.Tls12 | SslProtocols.Tls13,
        AllowRenegotiation = false,
        ClientCertificateRequired = policy.ClientCertificate is not null,
        RemoteCertificateValidationCallback = ClientCertificateValidation.Create(policy.ClientCertificate),
        CertificateChainPolicy = ClientCertificateValidation.ChainPolicy(policy.ClientCertificate),
    };

    // QUIC is TLS 1.3 only, so MinimumVersion and TcpAlpn don't apply.
    private static SslServerAuthenticationOptions CreateQuicOptions(ServerCertificate certificate, TlsPolicy policy) => new()
    {
        ServerCertificateContext = certificate.Context,
        ApplicationProtocols = [SslApplicationProtocol.Http3],
        ClientCertificateRequired = policy.ClientCertificate is not null,
        RemoteCertificateValidationCallback = ClientCertificateValidation.Create(policy.ClientCertificate),
        CertificateChainPolicy = ClientCertificateValidation.ChainPolicy(policy.ClientCertificate),
    };

    /// <summary>Adapts a <see cref="ClientCertificatePolicy"/> to SslStream's validation callback (built once per entry).</summary>
    private sealed class ClientCertificateValidation(ClientCertificatePolicy policy)
    {
        public static RemoteCertificateValidationCallback? Create(ClientCertificatePolicy? policy) =>
            policy is null ? null : new ClientCertificateValidation(policy).Validate;

        /// <summary>Chain building for client certificates is offline: by default it follows the AIA URL of a missing
        /// issuer, which would let any unauthenticated client make the proxy fetch a URL of its choosing
        /// mid-handshake (SSRF).</summary>
        // TODO: Authenticated Origin Pulls: X509ChainTrustMode.CustomRootTrust with Cloudflare's origin-pull CA.
        public static X509ChainPolicy? ChainPolicy(ClientCertificatePolicy? policy) =>
            policy is null
                ? null
                : new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck };

        private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return !policy.Required;
            }

            return policy.Validator is { } validator
                ? validator(sender, certificate, chain, errors)
                : errors == SslPolicyErrors.None;
        }
    }
}
