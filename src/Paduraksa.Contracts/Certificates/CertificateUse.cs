using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Certificates;

/// <summary>Why the config serves a certificate for a host.</summary>
[JsonConverter(typeof(ConfigEnumConverter<CertificateUseKind>))]
public enum CertificateUseKind
{
    /// <summary>An Auto host whose name this certificate covers best.</summary>
    Auto,

    /// <summary>A host that names this certificate (a Specific binding).</summary>
    Specific,

    /// <summary>The default certificate, for names nothing else covers.</summary>
    Default,
}

/// <summary>One place the latest config uses a certificate.</summary>
/// <param name="Kind">How it is chosen.</param>
/// <param name="HostId">The host; null for <see cref="CertificateUseKind.Default"/>.</param>
/// <param name="HostName">The host's first name, for display; null for <see cref="CertificateUseKind.Default"/>.</param>
/// <param name="ServerNames">For an Auto host: the names of it this certificate is the best match for.</param>
public sealed record CertificateUse(CertificateUseKind Kind, HostId? HostId = null, string? HostName = null, IReadOnlyList<string>? ServerNames = null)
{
    public IReadOnlyList<string> ServerNames { get; init; } = ServerNames ?? [];
}
