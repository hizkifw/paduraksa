using System.Globalization;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// An upstream's timeouts (docs/design/hot-path.md "Upstream timeouts"). Both are per-cluster values, so the request path reads them from
/// config it already has: the idle timeout is the cluster's <c>ForwarderRequestConfig.ActivityTimeout</c>, and the connect timeout is
/// a property of the HTTP client, which <c>HttpClientConfig</c> has no field for, so it rides in the cluster's metadata (like the pins)
/// where the client factory reads it and compares it before reusing a client.
/// </summary>
internal static class UpstreamTimeouts
{
    /// <summary>Cluster metadata key of a connect timeout that isn't <see cref="DefaultConnect"/>, in milliseconds.</summary>
    public const string ConnectMetadataKey = "paduraksa.connect-timeout-ms";

    public static TimeSpan DefaultConnect { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Long enough that no WebSocket or event stream that is still wanted goes quiet for it: chat, terminals and home-automation
    /// dashboards ping every few seconds to a few minutes, and a person who steps away from an idle session for an hour is rare.
    /// </summary>
    public static TimeSpan DefaultIdle { get; } = TimeSpan.FromHours(1);

    /// <summary>The idle timeout that means never.</summary>
    public static TimeSpan None { get; } = TimeSpan.Zero;

    public static bool IsValidConnect(TimeSpan connect) => connect >= TimeSpan.FromMilliseconds(100) && connect <= TimeSpan.FromMinutes(5);

    public static bool IsValidIdle(TimeSpan idle) => idle == None || (idle >= TimeSpan.FromSeconds(1) && idle <= TimeSpan.FromHours(24));

    /// <summary>The metadata entry of <paramref name="connect"/>; null for the default, which is no entry at all.</summary>
    internal static string? ConnectMetadata(TimeSpan connect) =>
        connect == DefaultConnect ? null : ((long)connect.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    internal static TimeSpan ConnectOf(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null
        && metadata.TryGetValue(ConnectMetadataKey, out var text)
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : DefaultConnect;
}
