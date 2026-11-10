using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>Which certificates an HTTPS destination may present. Plain-HTTP destinations ignore it.</summary>
[JsonConverter(typeof(ConfigEnumConverter<UpstreamTlsTrust>))]
public enum UpstreamTlsTrust
{
    /// <summary>A chain to the system's trusted roots, issued for the name sent (the client's <c>Host</c>).</summary>
    System,

    /// <summary>Exactly the certificates whose SHA-256 fingerprint is listed, whatever their name, chain or expiry: a
    /// self-signed certificate, trusted on purpose.</summary>
    Pinned,

    /// <summary>Any certificate. The traffic is still encrypted but not authenticated: anyone on the path to the upstream
    /// can read and change it.</summary>
    Any,
}
