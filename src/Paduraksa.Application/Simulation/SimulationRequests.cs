using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text;

using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Geo;
using Paduraksa.Proxy.Simulation;

namespace Paduraksa.Application.Simulation;

/// <summary>Stable error codes of a malformed <see cref="SimulationRequest"/> (<see cref="ConfigError.Code"/>).</summary>
public static class SimulationErrorCodes
{
    public const string UrlInvalid = "simulation.request.url";
    public const string ClientIpInvalid = "simulation.request.clientIp";
    public const string PeerIpInvalid = "simulation.request.peerIp";
    public const string TransportIpInvalid = "simulation.request.transportIp";
    public const string CountryInvalid = "simulation.request.country";
    public const string MethodInvalid = "simulation.request.method";
    public const string HeaderInvalid = "simulation.request.headers";
    public const string TlsInvalid = "simulation.request.tls";
    public const string TooLarge = "simulation.request.tooLarge";
    public const string Conflicting = "simulation.request.conflicting";
    public const string DraftRejected = "simulation.draft.rejected";
    public const string RevisionUnreadable = "simulation.revision.unreadable";
    public const string Busy = "simulation.busy";
    public const string StoreUnreadable = "simulation.store.unreadable";
}

/// <summary>Turns a <see cref="SimulationRequest"/> into the facts the gate simulator takes, filling in the defaults.</summary>
internal static class SimulationRequests
{
    /// <summary>Kestrel's default limit for the request line (<c>MaxRequestLineSize</c>).</summary>
    public const int MaxRequestLineBytes = 8 * 1024;

    /// <summary>Kestrel's default limit for the headers together (<c>MaxRequestHeadersTotalSize</c>).</summary>
    public const int MaxHeadersBytes = 32 * 1024;

    /// <summary>Kestrel's default limit for the number of headers (<c>MaxRequestHeaderCount</c>).</summary>
    public const int MaxHeaderCount = 100;

    /// <summary>A method this long is not an HTTP method.</summary>
    public const int MaxMethodLength = 32;

    /// <summary>The probe, or every problem found (paths are JSON pointers into the request).</summary>
    public static bool TryBuild(SimulationRequest request, out GateProbe? probe, out List<ConfigError> errors)
    {
        errors = [];
        probe = null;
        if (!TrySplitUrl(request.Url, out var https, out var host, out var path, out var query, out var problem, out var code))
        {
            errors.Add(new ConfigError("/url", code!, problem!));
        }

        var client = Address(request.ClientIp, "/clientIp", SimulationErrorCodes.ClientIpInvalid, errors);
        var peer = request.PeerIp is null ? null : Address(request.PeerIp, "/peerIp", SimulationErrorCodes.PeerIpInvalid, errors);
        var transport = request.TransportIp is null ? null : Address(request.TransportIp, "/transportIp", SimulationErrorCodes.TransportIpInvalid, errors);

        // Said in full rather than ignored: a field that changes nothing would make the answer look like it counted.
        if (request.TransportIp is not null && !request.ProxyProtocol)
        {
            errors.Add(new ConfigError("/transportIp", SimulationErrorCodes.Conflicting, "transportIp is the socket address behind a PROXY protocol header: set proxyProtocol too."));
        }

        if (request.PeerIp is not null && request.Arrival == SimulationArrival.Direct && !request.ProxyProtocol)
        {
            errors.Add(new ConfigError("/peerIp", SimulationErrorCodes.Conflicting, "A direct request's peer is the client: drop peerIp, or say the request comes through a proxy or with PROXY protocol."));
        }

        string? country = null;
        if (request.Country is { } named)
        {
            country = named.Trim().ToUpperInvariant();
            if (!Countries.TryParseRuleCode(country, out _))
            {
                errors.Add(new ConfigError("/country", SimulationErrorCodes.CountryInvalid, $"'{named}' isn't a country code (two letters from ISO 3166-1, T1 for Tor or XX for unknown)."));
            }
            else if (request.Arrival is not (SimulationArrival.CloudflareProxy or SimulationArrival.CloudflareTunnel))
            {
                errors.Add(new ConfigError("/country", SimulationErrorCodes.Conflicting, "The country comes from Cloudflare: set arrival to cloudflareProxy or cloudflareTunnel, or send a CF-IPCountry header yourself to see it ignored."));
            }
        }

        var method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim();
        if (method.Length > MaxMethodLength || !IsToken(method))
        {
            errors.Add(new ConfigError("/method", SimulationErrorCodes.MethodInvalid, $"'{method}' is not an HTTP method (a token of at most {MaxMethodLength} characters)."));
        }

        // Kestrel limits the request line in bytes on the wire, and a non-ASCII character takes more than one.
        if (errors.Count == 0
            && Encoding.UTF8.GetByteCount(method) + 1 + Encoding.UTF8.GetByteCount(path) + (query is null ? 0 : Encoding.UTF8.GetByteCount(query) + 1) + " HTTP/1.1".Length > MaxRequestLineBytes)
        {
            errors.Add(new ConfigError("/url", SimulationErrorCodes.TooLarge, $"The request line (method, path and query) can't exceed {MaxRequestLineBytes} bytes: Kestrel answers 431."));
        }

        List<ProbeHeader> headers = [];
        List<string> hostHeaders = [];
        long headerBytes = 0;
        if (request.Headers.Count > MaxHeaderCount)
        {
            errors.Add(new ConfigError("/headers", SimulationErrorCodes.TooLarge, $"At most {MaxHeaderCount} headers: Kestrel answers 431."));
        }

        for (var i = 0; i < request.Headers.Count; i++)
        {
            var header = request.Headers[i];
            if (string.IsNullOrEmpty(header.Name) || !IsToken(header.Name))
            {
                errors.Add(new ConfigError($"/headers/{i}/name", SimulationErrorCodes.HeaderInvalid, $"'{header.Name}' is not a header name."));
            }
            else if (header.Value is null || header.Value.AsSpan().ContainsAny('\r', '\n', '\0'))
            {
                errors.Add(new ConfigError($"/headers/{i}/value", SimulationErrorCodes.HeaderInvalid, "A header value can't contain line breaks."));
            }
            else
            {
                // Kestrel counts header octets against MaxRequestHeadersTotalSize; a browser sends a non-ASCII value as
                // UTF-8, so this is the wire count (and the conservative upper bound) rather than the character count.
                headerBytes += Encoding.UTF8.GetByteCount(header.Name) + Encoding.UTF8.GetByteCount(header.Value) + 4;
                if (string.Equals(header.Name, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    hostHeaders.Add(header.Value);
                }
                else
                {
                    headers.Add(new ProbeHeader(header.Name, header.Value));
                }
            }
        }

        if (headerBytes > MaxHeadersBytes)
        {
            errors.Add(new ConfigError("/headers", SimulationErrorCodes.TooLarge, $"The headers together can't exceed {MaxHeadersBytes} bytes: Kestrel answers 431."));
        }

        var version = https ? ProbeHttpVersion.Http2 : ProbeHttpVersion.Http11;
        var tls = SslProtocols.Tls13;
        if (request.Tls is { } context)
        {
            if (!https && (context.ServerName is not null || context.HttpVersion is not null || context.TlsVersion is not null))
            {
                errors.Add(new ConfigError("/tls", SimulationErrorCodes.TlsInvalid, "A plain-HTTP request has no TLS context: its listener speaks HTTP/1.1 only, and there is no handshake. Use an https URL, or drop tls."));
            }
            else
            {
                if (context.HttpVersion is { } requested)
                {
                    version = requested switch
                    {
                        SimulationHttpVersion.Http3 => ProbeHttpVersion.Http3,
                        SimulationHttpVersion.Http2 => ProbeHttpVersion.Http2,
                        _ => ProbeHttpVersion.Http11,
                    };
                }

                if (context.TlsVersion is { } requestedTls)
                {
                    tls = requestedTls == SimulationTlsVersion.Tls12 ? SslProtocols.Tls12 : SslProtocols.Tls13;
                }
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        probe = new GateProbe
        {
            Https = https,
            Host = host,
            HostHeaders = hostHeaders.Count == 0 ? null : hostHeaders,
            Path = path,
            Query = query,
            Method = method,
            Headers = headers,
            ClientAddress = client!,
            Arrival = request.Arrival switch
            {
                SimulationArrival.TrustedProxy => ProbeArrival.TrustedProxy,
                SimulationArrival.CloudflareProxy => ProbeArrival.CloudflareProxy,
                SimulationArrival.CloudflareTunnel => ProbeArrival.CloudflareTunnel,
                _ => ProbeArrival.Direct,
            },
            Country = country,
            PeerAddress = peer,
            ProxyProtocol = request.ProxyProtocol,
            TransportAddress = transport,
            HttpVersion = version,
            ServerName = request.Tls?.ServerName,
            TlsVersion = tls,
        };
        return true;
    }

    /// <summary>
    /// Splits <c>scheme://authority/path?query#fragment</c> by hand: the path must reach the gate as written (an
    /// <see cref="Uri"/> would resolve dot segments and decode escapes the gate has its own rules for).
    /// </summary>
    private static bool TrySplitUrl(
        string? url, out bool https, out string host, out string path, out string? query, out string? problem, out string? code)
    {
        https = false;
        host = string.Empty;
        path = "/";
        query = null;
        problem = null;
        code = SimulationErrorCodes.UrlInvalid;
        if (string.IsNullOrWhiteSpace(url))
        {
            problem = "A URL is required.";
            return false;
        }

        var text = url.Trim();
        if (Encoding.UTF8.GetByteCount(text) > MaxRequestLineBytes)
        {
            problem = $"The URL can't exceed {MaxRequestLineBytes} bytes: Kestrel answers 431.";
            code = SimulationErrorCodes.TooLarge;
            return false;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? string.Empty : text[..schemeEnd];
        if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            problem = "The URL must be absolute, with the scheme http or https (https://app.example.com/path).";
            return false;
        }

        https = scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        var fragment = text.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            text = text[..fragment];
        }

        var rest = text[(schemeEnd + 3)..];
        var authorityEnd = rest.AsSpan().IndexOfAny('/', '?');
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        var target = authorityEnd < 0 ? string.Empty : rest[authorityEnd..];
        if (authority.Contains('@', StringComparison.Ordinal) || authority.Contains('\\', StringComparison.Ordinal)
            || !Uri.TryCreate($"{(https ? "https" : "http")}://{authority}", UriKind.Absolute, out var parsed)
            || parsed.Host.Length == 0)
        {
            problem = "The URL has no valid host (user info and backslashes aren't allowed).";
            return false;
        }

        var name = parsed.HostNameType == UriHostNameType.IPv6 ? "[" + parsed.Host.Trim('[', ']') + "]" : parsed.IdnHost;
        host = parsed.IsDefaultPort ? name : $"{name}:{parsed.Port.ToString(CultureInfo.InvariantCulture)}";

        var queryStart = target.IndexOf('?', StringComparison.Ordinal);
        var rawPath = queryStart < 0 ? target : target[..queryStart];
        query = queryStart < 0 || queryStart == target.Length - 1 ? null : target[(queryStart + 1)..];
        path = rawPath.Length == 0 ? "/" : rawPath;
        if (path.IndexOfAny([' ', '\r', '\n', '\0']) >= 0 || HasControl(path))
        {
            problem = "The URL's path can't contain spaces or control characters: percent-encode them.";
            return false;
        }

        if (query is not null && (HasControl(query) || query.Contains(' ', StringComparison.Ordinal)))
        {
            problem = "The URL's query can't contain spaces or control characters: percent-encode them.";
            return false;
        }

        return true;
    }

    private static bool HasControl(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress? Address(string? text, string path, string code, List<ConfigError> errors)
    {
        // No zone ids (fe80::1%eth0): the gate's addresses carry none.
        if (text is not null && !text.Contains('%', StringComparison.Ordinal) && IPAddress.TryParse(text.Trim(), out var address))
        {
            return address;
        }

        errors.Add(new ConfigError(path, code, $"'{text}' is not an IPv4 or IPv6 address."));
        return null;
    }

    private static bool IsToken(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && "!#$%&'*+-.^_`|~".IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
