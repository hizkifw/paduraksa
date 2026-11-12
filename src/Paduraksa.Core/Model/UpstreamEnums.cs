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

/// <summary>What <c>Host</c> an upstream receives. On an HTTPS destination the name sent in SNI, and the name the destination's
/// certificate is checked against, follow it.</summary>
[JsonConverter(typeof(ConfigEnumConverter<UpstreamHostHeaderMode>))]
public enum UpstreamHostHeaderMode
{
    /// <summary>The client's own <c>Host</c> (or <c>:authority</c>), as behind nginx or Caddy.</summary>
    Original,

    /// <summary>The destination's own address: its host, and its port when it isn't the scheme's default.</summary>
    Destination,

    /// <summary>A fixed name, for an upstream that answers only to its own, such as a hosted service.</summary>
    Custom,
}
