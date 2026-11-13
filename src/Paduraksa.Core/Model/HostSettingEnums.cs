using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>Lowest TLS version a host accepts over TCP (QUIC is always TLS 1.3).</summary>
[JsonConverter(typeof(ConfigEnumConverter<TlsMinimumVersion>))]
public enum TlsMinimumVersion
{
    /// <summary>TLS 1.2 or newer.</summary>
    Tls12 = 0,

    /// <summary>TLS 1.3 only.</summary>
    Tls13 = 1,
}

/// <summary>ALPN protocols a host offers over TCP. QUIC always offers <c>h3</c>.</summary>
[JsonConverter(typeof(ConfigEnumConverter<TcpAlpn>))]
public enum TcpAlpn
{
    /// <summary><c>h2</c>, then <c>http/1.1</c>.</summary>
    Http2AndHttp11 = 0,

    /// <summary><c>http/1.1</c> only (for clients or upstream setups that misbehave on HTTP/2).</summary>
    Http11Only = 1,
}

/// <summary>What a host's responses put in <c>Server</c>.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ServerHeaderMode>))]
public enum ServerHeaderMode
{
    /// <summary><c>Server: Paduraksa</c>, with no version. Replaces an upstream's own header.</summary>
    Paduraksa,

    /// <summary><c>Server: &lt;Value&gt;</c>. Replaces an upstream's own header.</summary>
    Custom,

    /// <summary>The upstream's own header passes through untouched. Gate answers carry none.</summary>
    Upstream,

    /// <summary>No <c>Server</c> header at all: an upstream's own is removed too. HTTP/2 trailers aren't touched.</summary>
    Remove,
}

/// <summary>Where a request's ID (<c>X-Request-Id</c>) goes: to the upstream, back to the client, both or neither.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RequestIdPropagation>))]
public enum RequestIdPropagation : byte
{
    /// <summary>The upstream and the client's response (the built-in default).</summary>
    UpstreamAndResponse = 0,

    /// <summary>The upstream only: the client never learns the ID, and the app's log is the one to correlate with.</summary>
    UpstreamOnly = 1,

    /// <summary>The client's response only: the upstream sees no header and can't confuse it with its own tracing.</summary>
    ResponseOnly = 2,

    /// <summary>Neither: only the proxy's own log mentions it.</summary>
    Off = 3,
}

/// <summary>What <see cref="RequestIdPropagation"/> sends where.</summary>
public static class RequestIdPropagationExtensions
{
    /// <summary>Whether the ID goes to the upstream.</summary>
    public static bool SendsUpstream(this RequestIdPropagation propagation) =>
        propagation is RequestIdPropagation.UpstreamAndResponse or RequestIdPropagation.UpstreamOnly;

    /// <summary>Whether the ID goes back to the client.</summary>
    public static bool SendsResponse(this RequestIdPropagation propagation) =>
        propagation is RequestIdPropagation.UpstreamAndResponse or RequestIdPropagation.ResponseOnly;
}

/// <summary>How long a rate-limit policy's rate is measured over.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RateLimitPeriod>))]
public enum RateLimitPeriod
{
    /// <summary>Requests per second.</summary>
    Second,

    /// <summary>Requests per minute (the built-in period).</summary>
    Minute,

    /// <summary>Requests per hour.</summary>
    Hour,
}

/// <summary>What a rate-limit bucket is keyed by.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RateLimitKey>))]
public enum RateLimitKey
{
    /// <summary>One bucket per resolved client IP (IPv6 by /64).</summary>
    ClientIp,

    /// <summary>One bucket per client IP and canonical matching path.</summary>
    ClientIpAndPath,

    /// <summary>One bucket per request header value, with the client IP as the fallback.</summary>
    Header,
}

/// <summary>How an access list combines its address rule and its passwords.</summary>
[JsonConverter(typeof(ConfigEnumConverter<AccessListSatisfy>))]
public enum AccessListSatisfy
{
    /// <summary>The address must pass and, when the list has users, the credentials too (the built-in choice).</summary>
    All,

    /// <summary>Either passes: a trusted range can skip the password prompt.</summary>
    Any,
}

/// <summary>What a rule set does with a request its rule matches.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RuleAction>))]
public enum RuleAction
{
    /// <summary>Answer 403 and stop.</summary>
    Deny,

    /// <summary>Abort the connection or stream without an answer.</summary>
    Drop,

    /// <summary>Stop looking at this set's later rules and let the request go on: the exception a later deny can't reach.</summary>
    Allow,
}

/// <summary>How a rule's path condition reads its pattern. All four compare the canonical matching path ignoring ASCII case.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RulePathKind>))]
public enum RulePathKind
{
    /// <summary>The whole path equals the pattern.</summary>
    Exact,

    /// <summary>The path is the pattern or lies below it, segment by segment, as a route's prefix does.</summary>
    Prefix,

    /// <summary><c>*</c> stands for any run of characters inside a segment, <c>**</c> for any run across segments, and a
    /// <c>/**/</c> segment for none or more.</summary>
    Glob,

    /// <summary>A regular expression that must match somewhere in the path, run without backtracking.</summary>
    Regex,
}

/// <summary>Which provider a forward-auth endpoint is, so the preset can fill in what each one expects: the endpoint's path,
/// the headers it returns, where a browser signs in and what a decision depends on.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ForwardAuthPreset>))]
public enum ForwardAuthPreset
{
    /// <summary>Any other forward-auth endpoint: the address is the whole URL and nothing else is assumed.</summary>
    Custom,

    /// <summary>authentik's proxy outpost (<c>/outpost.goauthentik.io/auth/traefik</c>).</summary>
    Authentik,

    /// <summary>Authelia (<c>/api/authz/forward-auth</c>).</summary>
    Authelia,

    /// <summary>oauth2-proxy (<c>/oauth2/auth</c>, which answers 202 or 401).</summary>
    OAuth2Proxy,
}

/// <summary>What else, besides the session, a forward-auth endpoint's decision depends on: how far a remembered answer may be
/// reused.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ForwardAuthCacheScope>))]
public enum ForwardAuthCacheScope
{
    /// <summary>The same host, method and request target from the same client address with the same credentials: the provider
    /// may decide per path and method (Authelia's access-control rules), so nothing broader is safe.</summary>
    Request,

    /// <summary>Any request to the same host from the same client address with the same credentials: the provider decides per
    /// application (authentik, oauth2-proxy), so one answer holds for every path.</summary>
    Host,
}

/// <summary>The bounds of a forward-auth provider, shared by the validator, the compiler and the proxy.</summary>
public static class ForwardAuthLimits
{
    /// <summary>The longest wait for the endpoint's answer, in seconds.</summary>
    public const int MaxTimeoutSeconds = 30;

    /// <summary>The built-in wait for the endpoint's answer, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 5;

    /// <summary>The longest an allow is remembered, in seconds: a revoked session is honoured by the proxy for that long.</summary>
    public const int MaxCacheSeconds = 300;

    /// <summary>The built-in time an allow is remembered, in seconds.</summary>
    public const int DefaultCacheSeconds = 10;

    /// <summary>Header names in either list.</summary>
    public const int MaxHeaderNames = 32;

    /// <summary>Longest header name or <c>name*</c> prefix.</summary>
    public const int MaxHeaderNameLength = 64;

    /// <summary>Longest endpoint URL, sign-in URL and return parameter.</summary>
    public const int MaxUrlLength = 512;

    /// <summary>Calls to one provider's endpoint that may be running at once; a request that finds them all busy is answered 503.</summary>
    public const int MaxConcurrentCalls = 64;

    /// <summary>Client headers every request to an endpoint carries: the credentials it decides on and what it uses to tell a browser from a
    /// script. The rest of the client's headers (and every hop-by-hop one) are not sent.</summary>
    public static IReadOnlyList<string> StandardRequestHeaders { get; } =
        ["Accept", "Accept-Language", "Authorization", "Cookie", "Proxy-Authorization", "User-Agent", "X-Requested-With"];

    /// <summary>The most of an endpoint's refusal (a sign-in page) the proxy takes in to relay, in bytes.</summary>
    public const int MaxRelayedBodyBytes = 64 * 1024;
}

/// <summary>The bounds of a rule set, shared by the validator, the compiler and the proxy.</summary>
public static class RuleSetLimits
{
    /// <summary>Rules in one set: they are tried in order, and a set this long is a mistake.</summary>
    public const int MaxRules = 200;

    /// <summary>Longest path pattern, in characters.</summary>
    public const int MaxPatternLength = 512;

    /// <summary>Most glob tokens (literal characters and wildcards): the matcher keeps one bit of state per token.</summary>
    public const int MaxGlobTokens = 126;

    /// <summary>User-agent substrings in one rule.</summary>
    public const int MaxUserAgentTokens = 100;

    /// <summary>Longest user-agent substring.</summary>
    public const int MaxUserAgentTokenLength = 128;

    /// <summary>Methods in one rule.</summary>
    public const int MaxMethods = 16;

    /// <summary>How long one regular-expression match may run before it counts as no match.</summary>
    public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(50);
}

/// <summary>The bounds of an access list's users and realm, shared by the validator, the compiler and the proxy.</summary>
public static class AccessListLimits
{
    public const int MaxUsernameLength = 64;

    public const int MaxRealmLength = 64;

    /// <summary>The users of one list: each is a hash a request may have to check, and a document this large is a mistake.</summary>
    public const int MaxUsers = 1000;

    /// <summary>Longest password the dashboard, the API and the gate deal in (UTF-8 bytes).</summary>
    public const int MaxPasswordBytes = 256;
}

/// <summary>The bounds of the proof-of-work settings, shared by the validator, the compiler and the proxy.</summary>
public static class ProofOfWorkLimits
{
    /// <summary>Lowest difficulty: 8 leading zero bits, a few milliseconds.</summary>
    public const int MinDifficulty = 8;

    /// <summary>Highest difficulty: 28 leading zero bits, minutes of work on one core.</summary>
    public const int MaxDifficulty = 28;

    /// <summary>The built-in difficulty: 16 leading zero bits, well under a second on an ordinary laptop.</summary>
    public const int DefaultDifficulty = 16;

    /// <summary>Shortest token lifetime: an hour.</summary>
    public const int MinTokenLifetimeHours = 1;

    /// <summary>Longest token lifetime: 30 days.</summary>
    public const int MaxTokenLifetimeHours = 720;

    /// <summary>The built-in token lifetime: seven days.</summary>
    public const int DefaultTokenLifetimeHours = 168;
}

/// <summary>What a custom header rule does to its header.</summary>
[JsonConverter(typeof(ConfigEnumConverter<HeaderOperation>))]
public enum HeaderOperation : byte
{
    /// <summary>Replaces every value the header had with the rule's own.</summary>
    Set = 0,

    /// <summary>Adds the rule's value after any the header already has.</summary>
    Add = 1,

    /// <summary>Removes the header, whoever set it.</summary>
    Remove = 2,
}
