using System.Buffers;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// The <c>Host</c> an upstream receives, decided at compile time so the request transform only reads it (docs/design/config.md "Upstream Host header modes"). SNI needs
/// no setting of its own: the forwarder's HTTP client takes the name for the TLS handshake, and the certificate check, from the
/// request's <c>Host</c>, so they follow whatever this sends.
/// </summary>
/// <param name="Mode">Which name is sent.</param>
/// <param name="Name">With <see cref="UpstreamHostHeaderMode.Custom"/>: the exact string sent.</param>
public sealed record UpstreamHost(UpstreamHostHeaderMode Mode, string? Name = null)
{
    /// <summary>Cluster metadata key of a custom name, which the active health probe reads (the transform reads the route).</summary>
    public const string MetadataKey = "paduraksa.upstream-host";

    private static readonly SearchValues<char> NotInAHost = SearchValues.Create(" /\\?#@%");

    public static UpstreamHost Original { get; } = new(UpstreamHostHeaderMode.Original);

    public static UpstreamHost Destination { get; } = new(UpstreamHostHeaderMode.Destination);

    /// <summary>
    /// A name that can go on the wire as a <c>Host</c> as is: printable ASCII with nothing that would make it a path, a query, user
    /// information or a second header. The config document's rules are stricter; this is the compiler's own backstop.
    /// </summary>
    public static bool IsSendable(string? name) =>
        name is { Length: > 0 and <= 262 }
        && !name.AsSpan().ContainsAnyExcept(Core.Text.AsciiChars.Visible)
        && !name.AsSpan().ContainsAny(NotInAHost);

    /// <summary>What an HTTP client sends as <c>Host</c> for <paramref name="address"/>: the host, with the port unless it is the
    /// scheme's default.</summary>
    public static string OfDestination(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var host = address.HostNameType == UriHostNameType.IPv6 ? address.Host : address.IdnHost;
        return address.IsDefaultPort ? host : $"{host}:{address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
