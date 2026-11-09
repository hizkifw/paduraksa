using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.Http;

/// <summary>What the gate did with the host's <c>Server</c> header.</summary>
public enum ServerHeaderDecision : byte
{
    /// <summary>Ours is on the response. YARP's copy of an upstream's own is replaced by the response transform.</summary>
    Sent,

    /// <summary>The response carries none, and the response transform removes an upstream's own too.</summary>
    Removed,

    /// <summary>The upstream's own passes through; a gate answer carries none.</summary>
    Upstream,
}

/// <summary>
/// The <c>Server</c> header (PLAN §14 M6). The compiler builds one value per host, and the gate applies it next to
/// <c>Alt-Svc</c> and HSTS, before it answers or forwards, so every response the host gives carries the same one. The
/// default names the product without a version.
/// </summary>
public static class ServerHeader
{
    /// <summary>The built-in name, and the header <see cref="ServerHeaderMode.Paduraksa"/> sends.</summary>
    public const string Product = "Paduraksa";

    /// <summary>The header's name.</summary>
    public const string HeaderName = "Server";

    /// <summary>The longest custom value accepted, in characters.</summary>
    public const int MaxValueLength = 128;

    /// <summary>
    /// The header value for one host. Compile time: builds the string once, then every response reuses it. Empty for
    /// <see cref="ServerHeaderMode.Upstream"/> and <see cref="ServerHeaderMode.Remove"/>, and for a custom mode with no
    /// value (which validation and the compiler refuse).
    /// </summary>
    public static StringValues ValueOf(HostServerHeaderSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Mode switch
        {
            ServerHeaderMode.Paduraksa => new StringValues(Product),
            ServerHeaderMode.Custom => string.IsNullOrEmpty(spec.Value) ? StringValues.Empty : new StringValues(spec.Value),
            _ => StringValues.Empty,
        };
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a custom header value: 1-128 visible ASCII characters or spaces, with no
    /// leading or trailing space and no control characters. RFC 9110 §10.2.4's grammar allows more; this is the simple,
    /// safe subset a host setting needs.
    /// </summary>
    public static bool IsValidValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength
            || value[0] == ' ' || value[^1] == ' ')
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the host's precomputed value. 0 B: the shared value is stored as is, or one absent header is removed.
    /// </summary>
    /// <param name="context">The response being answered.</param>
    /// <param name="hostValue">The host's precomputed value (<see cref="Snapshot.CompiledHost.Server"/>).</param>
    /// <param name="mode">The host's mode.</param>
    public static ServerHeaderDecision Apply(HttpContext context, StringValues hostValue, ServerHeaderMode mode)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (mode)
        {
            case ServerHeaderMode.Paduraksa:
            case ServerHeaderMode.Custom:
                if (StringValues.IsNullOrEmpty(hostValue))
                {
                    context.Response.Headers.Remove(HeaderName);
                    return ServerHeaderDecision.Removed;
                }

                context.Response.Headers[HeaderName] = hostValue;
                return ServerHeaderDecision.Sent;
            case ServerHeaderMode.Remove:
                context.Response.Headers.Remove(HeaderName);
                return ServerHeaderDecision.Removed;
            default:
                return ServerHeaderDecision.Upstream;
        }
    }
}
