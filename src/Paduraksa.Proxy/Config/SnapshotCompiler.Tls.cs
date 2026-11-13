using Paduraksa.Core.Model;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Config;

/// <summary>The certificate table and the TLS policy a host's names are served with.</summary>
public static partial class SnapshotCompiler
{
    private static ServerCertificate? ResolveCertificate(CertificateId? id, CompileErrorSource where, IServerCertificateResolver? resolver, List<CompileError> errors)
    {
        if (id is not { } certificateId)
        {
            return null;
        }

        if (certificateId.IsEmpty)
        {
            errors.Add(new(where, "the certificate ID can't be empty (leave it unset to use the default)"));
            return null;
        }

        if (resolver is null)
        {
            errors.Add(new(where, $"certificate '{certificateId}' can't be resolved: no certificate source is configured"));
            return null;
        }

        if (!resolver.TryResolve(certificateId, out var certificate))
        {
            errors.Add(new(where, $"certificate '{certificateId}' doesn't exist"));
            return null;
        }

        return certificate;
    }

    /// <summary>The certificates a host names for individual names (<see cref="HostSpec.NameCertificates"/>), by
    /// normalised name.</summary>
    private static Dictionary<string, ServerCertificate> ResolveNameCertificates(
        HostSpec spec, CompileErrorSource where, IServerCertificateResolver? resolver, List<CompileError> errors)
    {
        Dictionary<string, ServerCertificate> resolved = new(StringComparer.Ordinal);
        if (spec.NameCertificates is not { Count: > 0 } named)
        {
            return resolved;
        }

        if (!spec.Https)
        {
            errors.Add(new(where, "a host without HTTPS can't name a certificate"));
            return resolved;
        }

        HashSet<string> hostNames = new(StringComparer.Ordinal);
        foreach (var name in spec.Names ?? [])
        {
            if (HostNames.TryNormalize(name, out var normalizedName, out _, out _))
            {
                hostNames.Add(normalizedName);
            }
        }

        foreach (var (name, id) in named)
        {
            if (!HostNames.TryNormalize(name, out var normalized, out var kind, out _) || !hostNames.Contains(normalized))
            {
                errors.Add(new(where, $"a certificate is named for '{name}', which is not one of the host's names"));
            }
            else if (kind == HostNameKind.IPv6Literal || (kind == HostNameKind.Exact && IpParser.TryParseIPv4(normalized, out _)))
            {
                errors.Add(new(where, $"a certificate is named for the IP literal '{name}', which never reaches the certificate table"));
            }
            else if (ResolveCertificate(id, where, resolver, errors) is { } certificate)
            {
                resolved[normalized] = certificate;
            }
        }

        return resolved;
    }

    /// <summary>The server names (<see cref="ServerNamesOf"/>) grouped by the certificate they get: the one named for
    /// the name, else the host's. Groups keep the order of their first name.</summary>
    private static List<(ServerCertificate Certificate, List<string> Names)> GroupByCertificate(
        List<string> names, ServerCertificate? hostCertificate, Dictionary<string, ServerCertificate> nameCertificates)
    {
        List<(ServerCertificate Certificate, List<string> Names)> groups = [];
        foreach (var name in names)
        {
            var certificate = nameCertificates.GetValueOrDefault(name) ?? hostCertificate;
            if (certificate is null)
            {
                continue;
            }

            var group = groups.FindIndex(g => ReferenceEquals(g.Certificate, certificate));
            if (group < 0)
            {
                groups.Add((certificate, [name]));
            }
            else
            {
                groups[group].Names.Add(name);
            }
        }

        return groups;
    }

    /// <summary>Equal specs give equal policies (records), so hosts with the same settings share one entry per certificate.</summary>
    private static TlsPolicy CompileTlsPolicy(HostTlsSpec? spec, CompileErrorSource where, List<CompileError> errors, ClientCertificatePolicy? clientCertificate = null)
    {
        spec ??= HostTlsSpec.Default;
        if (!Enum.IsDefined(spec.MinimumVersion) || !Enum.IsDefined(spec.TcpAlpn))
        {
            // TlsPolicy would throw; report it like any other config error.
            errors.Add(new(where, $"unknown TLS setting (minimum version {spec.MinimumVersion}, TCP ALPN {spec.TcpAlpn})"));
            return TlsPolicy.Default;
        }

        return new TlsPolicy
        {
            MinimumVersion = spec.MinimumVersion,
            TcpAlpn = spec.TcpAlpn,

            // Cloudflare doesn't connect to origins over QUIC and a QUIC handshake here would carry no origin-pull
            // certificate, so a host that requires one is left out of HTTP/3 whatever its own setting says.
            Http3Enabled = spec.Http3 && clientCertificate is null,
            ClientCertificate = clientCertificate,
        };
    }

    /// <summary>A host's DNS names, normalised: what goes into the certificate table. IP literals are left out:
    /// clients never send them as SNI (an IP-literal URL sends none and gets the default certificate); names without one
    /// ride the default certificate's entry or are rejected.</summary>
    private static List<string> ServerNamesOf(IReadOnlyList<string> names)
    {
        List<string> serverNames = new(names.Count);
        foreach (var name in names)
        {
            if (HostNames.TryNormalize(name, out var normalized, out var kind, out _)
                && kind != HostNameKind.IPv6Literal
                && !(kind == HostNameKind.Exact && IpParser.TryParseIPv4(normalized, out _)))
            {
                serverNames.Add(normalized);
            }
        }

        return serverNames;
    }

    /// <summary>
    /// Adds a host's server names (<see cref="ServerNamesOf"/>) to the certificate table. Runs only after the host
    /// table accepted every one of the host's names: both tables apply the same rules (<see cref="DnsNames"/>) and
    /// reject the same duplicates, so nothing is reported twice, and an error here means they disagree (a bug).
    /// </summary>
    private static void AddServerNames(
        CertificateTableBuilder certificates,
        List<string> serverNames,
        ServerCertificate certificate,
        TlsPolicy tls,
        CompileErrorSource where,
        List<CompileError> errors)
    {
        try
        {
            certificates.Add(serverNames, certificate, tls);
        }
        catch (ArgumentException ex)
        {
            errors.Add(new(where, ex.Message));
        }
    }
}
