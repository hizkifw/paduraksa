using System.Security.Authentication;

using Paduraksa.Core.Model;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>How a request got into the trap.</summary>
public enum HoneypotEntry : byte
{
    /// <summary>A request under the prefix of a host that serves the <c>Disallow</c> line: a crawler that read robots.txt would not have come.</summary>
    Robots,

    /// <summary>A request under the prefix of a host whose robots.txt passes through, so nothing told a crawler to stay out.</summary>
    Prefix,

    /// <summary>A rule set's honeypot action answered a path outside the prefix.</summary>
    Rule,
}

/// <summary>What the trap's proof-of-work challenge did for a request.</summary>
public enum HoneypotPowOutcome : byte
{
    /// <summary>The page wasn't behind a challenge.</summary>
    None,

    /// <summary>The challenge page was answered instead of the page.</summary>
    Shown,

    /// <summary>The verify request carried a valid solution (<see cref="HoneypotHit.SolveMilliseconds"/> says how long it took).</summary>
    Solved,

    /// <summary>The page behind a challenge was served on a valid cookie.</summary>
    Passed,
}

/// <summary>
/// One trap request, as the gate saw it. A struct, so recording a miss costs nothing and a hit costs only its strings.
/// </summary>
/// <param name="Time">When the request arrived.</param>
/// <param name="Host">The host's ID.</param>
/// <param name="ClientIp">The client address in full, as the gate resolved it.</param>
/// <param name="UserAgent">The <c>User-Agent</c> header; empty when absent.</param>
/// <param name="Canary">The visitor's canary phrase, the one woven into the post text it was served.</param>
/// <param name="Entry">How it got in.</param>
/// <param name="PageKind">What the URL named; the verify request carries the page the visitor was sent back to.</param>
/// <param name="LinkKind">The kind of link the URL was published as.</param>
/// <param name="PageId">The page's ID, the seed of its content.</param>
/// <param name="ParentId">The page whose link this URL is; 0 when there is none.</param>
/// <param name="Status">The status answered.</param>
/// <param name="Pow">What the challenge did.</param>
/// <param name="SolveMilliseconds">With <see cref="HoneypotPowOutcome.Solved"/>: from the challenge's issue to its verification, to the second; else 0.</param>
/// <param name="Protocol">The HTTP version as the request line says it (<c>HTTP/1.1</c>, <c>HTTP/2</c>).</param>
/// <param name="TlsVersion">The negotiated TLS version; <see cref="SslProtocols.None"/> on plain HTTP.</param>
/// <param name="Accept">The <c>Accept</c> header, or null.</param>
/// <param name="AcceptLanguage">The <c>Accept-Language</c> header, or null.</param>
/// <param name="SecFetchSite">The <c>Sec-Fetch-Site</c> header, or null.</param>
/// <param name="SecFetchMode">The <c>Sec-Fetch-Mode</c> header, or null.</param>
/// <param name="SecFetchDest">The <c>Sec-Fetch-Dest</c> header, or null.</param>
/// <param name="SecFetchUser">The <c>Sec-Fetch-User</c> header, or null.</param>
/// <param name="Referer">The <c>Referer</c> header, or null.</param>
public readonly record struct HoneypotHit(
    DateTimeOffset Time,
    HostId Host,
    IpKey ClientIp,
    string UserAgent,
    string Canary,
    HoneypotEntry Entry,
    HoneypotPageKind PageKind,
    HoneypotLinkKind LinkKind,
    ulong PageId,
    ulong ParentId,
    int Status,
    HoneypotPowOutcome Pow,
    int SolveMilliseconds,
    string Protocol,
    SslProtocols TlsVersion,
    string? Accept,
    string? AcceptLanguage,
    string? SecFetchSite,
    string? SecFetchMode,
    string? SecFetchDest,
    string? SecFetchUser,
    string? Referer);

/// <summary>
/// Where the gate reports what visitors do in the trap. Called from the request, so an implementation hands the work to a queue and
/// returns: it must not block, wait or throw.
/// </summary>
public interface IHoneypotSink
{
    /// <summary>A request the trap answered.</summary>
    void Record(in HoneypotHit hit);

    /// <summary>A <c>GET</c> or <c>HEAD /robots.txt</c> on a host with a trap on, so a session can tell whether the visitor read the file first.</summary>
    void RobotsFetched(HostId host, IpKey clientIp, string userAgent);
}

/// <summary>The sink until something records: it drops everything.</summary>
public sealed class NullHoneypotSink : IHoneypotSink
{
    public static NullHoneypotSink Instance { get; } = new();

    public void Record(in HoneypotHit hit)
    {
    }

    public void RobotsFetched(HostId host, IpKey clientIp, string userAgent)
    {
    }
}
