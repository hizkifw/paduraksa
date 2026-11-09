using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Tls;

/// <summary>Why a host's HSTS header was or wasn't set on a response.</summary>
public enum HstsDecision : byte
{
    /// <summary>The host has HSTS off: nothing of ours is sent, so an upstream's own header passes through untouched.</summary>
    Off = 0,

    /// <summary>Ours is on the response; YARP then leaves an upstream's own out (<c>HttpTransformer</c> skips it when one
    /// is already set).</summary>
    Sent,

    /// <summary>The request isn't HTTPS as the trusted proxies see it: HSTS goes on HTTPS responses only.</summary>
    PlainHttp,

    /// <summary>The request's host name is an IP literal or a loopback name, which browsers must not pin.</summary>
    ExemptName,
}

/// <summary>
/// The <c>Strict-Transport-Security</c> header Paduraksa sends (PLAN §14 M6). The compiler builds one value per host
/// (<see cref="ValueOf"/>) and the gate applies it (<see cref="Apply"/>) next to <c>Alt-Svc</c>, after the TLS check, so
/// every response the host then gets carries it: forwarded ones, the HTTPS redirect, a Redirect or Respond answer and a
/// policy's refusal. A 421 from that TLS check deliberately gets none: the client is on a connection it must leave, and
/// pinning the name for a year on the strength of a misdirected request is not this host's to do.
/// <para><b>When it goes:</b> only when the <b>trusted</b> request scheme is HTTPS (<see cref="Net.RequestScheme"/>, so a
/// TLS-terminating trusted proxy counts and plain HTTP never does), and never for an IP literal or a loopback name
/// (<c>localhost</c>, <c>*.localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>): browsers ignore HSTS for those, and a remembered
/// <c>localhost</c> would break every local development server behind it. Unlike <c>Alt-Svc</c>, it goes over HTTP/3 too:
/// that is a transport a browser chose, and HSTS is about the host's scheme, not its protocol.</para>
/// </summary>
public static class HstsHeader
{
    /// <summary>One year, the value the preload lists require and the built-in default.</summary>
    public const int DefaultMaxAgeSeconds = 31_536_000;

    /// <summary>Two years: beyond this a host is pinned long after whoever set it can remember why.</summary>
    public const int MaxMaxAgeSeconds = 63_072_000;

    /// <summary>The shortest <c>max-age</c> a preload list accepts (a year).</summary>
    public const int PreloadMinMaxAgeSeconds = 31_536_000;

    private const string LocalhostSuffix = ".localhost";
    private const string Localhost = "localhost";

    /// <summary>
    /// The header value for one host, e.g. <c>max-age=31536000; includeSubDomains</c>. Compile time: builds the string
    /// once, then every response reuses it. Empty when HSTS is off.
    /// </summary>
    public static StringValues ValueOf(HostHstsSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!spec.Enabled)
        {
            return StringValues.Empty;
        }

        string value = "max-age=" + spec.MaxAgeSeconds.ToString(CultureInfo.InvariantCulture);
        if (spec.IncludeSubDomains)
        {
            value += "; includeSubDomains";
        }

        if (spec.Preload)
        {
            value += "; preload";
        }

        return new StringValues(value);
    }

    /// <summary>
    /// Sets <c>Strict-Transport-Security</c> to the host's precomputed value when it applies. 0 B: no string is built,
    /// the shared value is stored as is. Sent over HTTP/3 as well, unlike <c>Alt-Svc</c>: HSTS is about the host's scheme,
    /// and an HTTP/3 request is already HTTPS.
    /// </summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="hostValue">The host's precomputed value (<see cref="Snapshot.CompiledHost.Hsts"/>).</param>
    /// <param name="isHttps">The trusted request scheme (<see cref="Gate.GateRequestState.IsHttps"/>).</param>
    /// <param name="requestHostName">The <c>Host</c> header's name, port and trailing dot stripped
    /// (<see cref="HostTable.HostName"/>).</param>
    public static HstsDecision Apply(HttpContext context, StringValues hostValue, bool isHttps, ReadOnlySpan<char> requestHostName)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (StringValues.IsNullOrEmpty(hostValue))
        {
            return HstsDecision.Off;
        }

        if (!isHttps)
        {
            return HstsDecision.PlainHttp;
        }

        if (IsExemptName(requestHostName))
        {
            return HstsDecision.ExemptName;
        }

        context.Response.Headers.StrictTransportSecurity = hostValue;
        return HstsDecision.Sent;
    }

    /// <summary>
    /// True for a name HSTS must never be sent for: a bracketed IPv6 literal (<c>[::1]</c> included), a loopback name
    /// (<c>localhost</c> and anything under <c>.localhost</c>, which browsers resolve to loopback), or a name a browser reads
    /// as an IPv4 address in any of its spellings: every label all digits (<c>127.0.0.1</c>, <c>127.1</c>, <c>0177.0.0.1</c>,
    /// <c>1.2.3</c>, <c>2130706433</c>) or hexadecimal (<c>0x7f.1</c>, <c>0x7f000001</c>). 0 B: a span check, never a parse.
    /// </summary>
    public static bool IsExemptName(ReadOnlySpan<char> hostName)
    {
        if (hostName.IsEmpty)
        {
            // Nothing to pin: a request without a usable name can't be a host anyway.
            return true;
        }

        // An IPv6 literal keeps its brackets (HostTable.HostName); every IPv6 address is an IP literal.
        if (hostName[0] == '[')
        {
            return true;
        }

        if (hostName.Equals(Localhost, StringComparison.OrdinalIgnoreCase)
            || hostName.EndsWith(LocalhostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Label by label: a browser resolves a name whose every label is a number (decimal, or hexadecimal with 0x) as an
        // address, however many labels there are (127.1, 1.2.3, 0177.0.0.1) and with or without dots (2130706433, 0x7f).
        // One label that isn't a number makes it a DNS name, which is the kind HSTS is for.
        int start = 0;
        while (true)
        {
            int dot = hostName[start..].IndexOf('.');
            ReadOnlySpan<char> label = dot < 0 ? hostName[start..] : hostName[start..(start + dot)];
            if (!IsNumericLabel(label))
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            start += dot + 1;
        }

        static bool IsNumericLabel(ReadOnlySpan<char> label)
        {
            if (label.IsEmpty)
            {
                return false;
            }

            if (label.Length > 2 && label[0] == '0' && (label[1] is 'x' or 'X'))
            {
                foreach (char c in label[2..])
                {
                    if (!char.IsAsciiHexDigit(c))
                    {
                        return false;
                    }
                }

                return true;
            }

            return label.IndexOfAnyExceptInRange('0', '9') < 0;
        }
    }
}
