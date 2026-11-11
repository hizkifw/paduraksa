using Yarp.ReverseProxy.Forwarder;

namespace Paduraksa.Proxy.Metrics;

/// <summary>Names and classes of YARP's <see cref="ForwarderError"/> values, for metrics.</summary>
public static class ForwarderErrors
{
    private static readonly string[] Names =
    [
        "none", "request", "request_timed_out", "request_canceled", "request_body_canceled", "request_body_client", "request_body_destination",
        "response_headers", "response_body_canceled", "response_body_client", "response_body_destination", "upgrade_request_canceled",
        "upgrade_request_client", "upgrade_request_destination", "upgrade_response_canceled", "upgrade_response_client",
        "upgrade_response_destination", "no_available_destinations", "request_creation", "upgrade_activity_timeout",
    ];

    /// <summary>The metric label for <paramref name="error"/>, e.g. <c>response_body_destination</c>; <c>unknown</c> for a value this build doesn't know.</summary>
    public static string NameOf(int error) => (uint)error < (uint)Names.Length ? Names[error] : "unknown";

    /// <summary>
    /// Whether the failure is the upstream's (or the proxy's reach to it): a refused or timed-out connection, a broken
    /// response, no destination to try. A client that went away mid-transfer, or cancelled, is not.
    /// </summary>
    public static bool IsUpstreamFault(ForwarderError error) => error switch
    {
        ForwarderError.None or ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled or ForwarderError.RequestBodyClient
            or ForwarderError.ResponseBodyCanceled or ForwarderError.ResponseBodyClient or ForwarderError.UpgradeRequestCanceled
            or ForwarderError.UpgradeRequestClient or ForwarderError.UpgradeResponseCanceled or ForwarderError.UpgradeResponseClient => false,
        _ => true,
    };

    /// <summary>The number of values a counter table makes room for; a build with a longer enum counts the overflow as the last one.</summary>
    internal static int Clamp(int error) => Math.Min(error, UpstreamMetric.ErrorKinds - 1);
}
