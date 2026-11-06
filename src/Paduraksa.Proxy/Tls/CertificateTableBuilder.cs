using System.Collections.Frozen;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Builds a <see cref="CertificateTable"/> (cold path, snapshot compile time). Names are validated and normalised
/// (lowercase, trailing dot dropped); a name configured twice is an error rather than "last one wins".
/// A failed <see cref="Add"/> leaves the builder unchanged.
/// </summary>
public sealed class CertificateTableBuilder
{
    private readonly Dictionary<string, CertificateTableEntry> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CertificateTableEntry> _wildcard = new(StringComparer.Ordinal);
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
        var normalized = new List<(string Key, bool Wildcard)>();
        var seen = new HashSet<(string, bool)>();
        foreach (var name in names)
        {
            var key = ServerNames.Normalize(name, out var wildcard);
            var table = wildcard ? _wildcard : _exact;
            if (table.ContainsKey(key) || !seen.Add((key, wildcard)))
            {
                throw new ArgumentException(
                    $"The server name '{name}' is configured more than once (names are case-insensitive and ignore a trailing dot). Each name must belong to exactly one host.",
                    nameof(names));
            }

            normalized.Add((key, wildcard));
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("A host needs at least one server name.", nameof(names));
        }

        var entry = GetOrCreateEntry(certificate, policy);
        foreach (var (key, wildcard) in normalized)
        {
            (wildcard ? _wildcard : _exact).Add(key, entry);
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
            _exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            _wildcard.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            _default,
            [.. entries],
            [.. options]);
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

    // ApplicationProtocols is ALWAYS set: both Kestrel transports write their own ALPN into a returned options object
    // whose list is null, which breaks the other transport when instances are shared (spike S1, trap 1).
    private static SslServerAuthenticationOptions CreateTcpOptions(ServerCertificate certificate, TlsPolicy policy) => new()
    {
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
    };

    // QUIC is TLS 1.3 only, so MinimumVersion and TcpAlpn don't apply.
    private static SslServerAuthenticationOptions CreateQuicOptions(ServerCertificate certificate, TlsPolicy policy) => new()
    {
        ServerCertificateContext = certificate.Context,
        ApplicationProtocols = [SslApplicationProtocol.Http3],
        ClientCertificateRequired = policy.ClientCertificate is not null,
        RemoteCertificateValidationCallback = ClientCertificateValidation.Create(policy.ClientCertificate),
    };

    /// <summary>Adapts a <see cref="ClientCertificatePolicy"/> to SslStream's validation callback (built once per entry).</summary>
    private sealed class ClientCertificateValidation(ClientCertificatePolicy policy)
    {
        public static RemoteCertificateValidationCallback? Create(ClientCertificatePolicy? policy) =>
            policy is null ? null : new ClientCertificateValidation(policy).Validate;

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
