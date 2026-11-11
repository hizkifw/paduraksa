using System.Collections.Frozen;
using System.Text;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The words for the config document's field names (<c>forceHttps</c> is "HTTPS redirect") and enum values (<c>http2AndHttp11</c> is
/// "HTTP/2 and HTTP/1.1"), for diffs and error locations. A field without an entry is spelt out from its camelCase name.
/// </summary>
public static class FieldLabels
{
    private static readonly FrozenDictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["apiVersion"] = "Format",
        ["names"] = "Hostnames",
        ["kind"] = "Kind",
        ["enabled"] = "Enabled",
        ["profileId"] = "Profile",
        ["certificate"] = "Certificate",
        ["certificateId"] = "Certificate",
        ["mode"] = "Mode",
        ["settings"] = "Settings",
        ["forceHttps"] = "HTTPS redirect",
        ["tls"] = "TLS",
        ["minimumVersion"] = "Minimum TLS version",
        ["tcpAlpn"] = "Protocols over TCP",
        ["http3"] = "HTTP/3",
        ["serverHeader"] = "Server header",
        ["value"] = "Value",
        ["proofOfWork"] = "Browser check",
        ["difficulty"] = "Difficulty",
        ["tokenLifetimeHours"] = "Token lifetime",
        ["rateLimit"] = "Rate limit",
        ["policyId"] = "Policy",
        ["requests"] = "Requests",
        ["per"] = "Per",
        ["burst"] = "Burst",
        ["key"] = "Count by",
        ["accessList"] = "Access list",
        ["accessListId"] = "Access list",
        ["accessLists"] = "Access lists",
        ["allow"] = "Allowed addresses",
        ["deny"] = "Denied addresses",
        ["ip"] = "Addresses",
        ["dropDenied"] = "Drop denied requests",
        ["basicAuth"] = "Passwords",
        ["realm"] = "Realm",
        ["users"] = "Users",
        ["satisfy"] = "Combine",
        ["passCredentialsUpstream"] = "Send credentials upstream",
        ["requestLimits"] = "Request limits",
        ["maxBodySize"] = "Body size",
        ["maxHeaderCount"] = "Header lines",
        ["maxHeaderBytes"] = "Header size",
        ["maxUrlLength"] = "URL length",
        ["accessLog"] = "Access log",
        ["requestIdPropagation"] = "Request ID",
        ["routes"] = "Routes",
        ["redirect"] = "Redirect",
        ["target"] = "Target",
        ["statusCode"] = "Status",
        ["preservePath"] = "Keep the path",
        ["respond"] = "Fixed answer",
        ["body"] = "Body",
        ["contentType"] = "Content type",
        ["tags"] = "Tags",
        ["notes"] = "Notes",
        ["upstreamId"] = "Upstream",
        ["order"] = "Order",
        ["match"] = "Match",
        ["path"] = "Path",
        ["name"] = "Name",
        ["destinations"] = "Destinations",
        ["address"] = "Address",
        ["loadBalancing"] = "Load balancing",
        ["sessionAffinity"] = "Sticky sessions",
        ["passiveHealth"] = "Passive health checks",
        ["reactivationPeriod"] = "Back in rotation after",
        ["activeHealth"] = "Active health checks",
        ["interval"] = "Interval",
        ["timeout"] = "Timeout",
        ["maxConnectionsPerServer"] = "Connections per destination",
        ["defaults"] = "Defaults",
        ["unknownHost"] = "Unknown hosts",
        ["defaultCertificateId"] = "Default certificate",
        ["trustedProxies"] = "Trusted proxies",
        ["proxies"] = "Proxies",
        ["header"] = "Header",
        ["useTrueClientIp"] = "True-Client-IP",
        ["forwardedForFallback"] = "X-Forwarded-For fallback",
        ["trustForwardedProto"] = "Trust X-Forwarded-Proto",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["proxy"] = "Proxy",
        ["redirect"] = "Redirect",
        ["respond"] = "Fixed answer",
        ["admin"] = "Admin",
        ["auto"] = "Automatic",
        ["specific"] = "Specific certificate",
        ["none"] = "None (HTTP only)",
        ["prefix"] = "Prefix",
        ["exact"] = "Exact",
        ["tls12"] = "TLS 1.2",
        ["tls13"] = "TLS 1.3",
        ["http2AndHttp11"] = "HTTP/2 and HTTP/1.1",
        ["http11Only"] = "HTTP/1.1 only",
        ["powerOfTwoChoices"] = "Power of two choices",
        ["roundRobin"] = "Round robin",
        ["random"] = "Random",
        ["leastRequests"] = "Least requests",
        ["firstAlphabetical"] = "First alphabetical",
        ["notFound"] = "404 Not Found",
        ["misdirectedRequest"] = "421 Misdirected Request",
        ["close"] = "Close the connection",
        ["off"] = "Off",
        ["generic"] = "Generic proxy",
        ["cloudflareProxy"] = "Cloudflare proxy",
        ["cloudflareTunnel"] = "Cloudflare Tunnel",
        ["upstreamAndResponse"] = "Upstream and the client",
        ["upstreamOnly"] = "Upstream only",
        ["responseOnly"] = "The client only",
        ["second"] = "second",
        ["minute"] = "minute",
        ["hour"] = "hour",
        ["clientIp"] = "Client IP",
        ["clientIpAndPath"] = "Client IP and path",
        ["header"] = "Header",
        ["paduraksa"] = "Paduraksa",
        ["custom"] = "Custom value",
        ["upstream"] = "The upstream's header",
        ["remove"] = "Remove",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string Of(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Fields.TryGetValue(field, out var label) ? label : Spell(field);
    }

    /// <summary>The words for an enum value as the document spells it; anything else as it is.</summary>
    public static string Value(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Values.TryGetValue(value, out var label) ? label : value;
    }

    /// <summary><c>someFieldName</c> as "Some field name".</summary>
    private static string Spell(string camel)
    {
        if (camel.Length == 0)
        {
            return camel;
        }

        StringBuilder text = new(camel.Length + 4);
        text.Append(char.ToUpperInvariant(camel[0]));
        for (var i = 1; i < camel.Length; i++)
        {
            var c = camel[i];
            if (char.IsUpper(c))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
