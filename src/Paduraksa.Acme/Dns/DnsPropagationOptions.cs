using System.Net;

namespace Paduraksa.Acme.Dns;

/// <summary>Tunables of <see cref="AuthoritativeDnsPropagationCheck"/>. The defaults suit the real internet. A record, so a variation is <c>options with { ... }</c>.</summary>
public sealed record DnsPropagationOptions
{
    /// <summary>How long the wait lasts in all. Providers publish in seconds to a couple of minutes; five minutes is generous.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The pause before the second round of queries. The first round runs at once, because many providers publish instantly.</summary>
    public TimeSpan InitialInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The pause grows by <see cref="BackoffFactor"/> each round, up to this.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The growth of the pause between rounds.</summary>
    public double BackoffFactor { get; init; } = 1.5;

    /// <summary>How long one query to one server may take. A slow or filtered server costs this much per round, in parallel with the others.</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A name server that has not given an authoritative answer in this many rounds in a row (silent, refusing, filtered by a
    /// firewall) is left out of the check, so a dead secondary cannot hold issuance for the whole <see cref="Timeout"/>. If
    /// every server is left out the result is <see cref="DnsPropagationOutcome.Unverifiable"/>.
    /// </summary>
    public int UnreachableRounds { get; init; } = 3;

    /// <summary>
    /// The recursive resolvers that find the zone's name servers and their addresses. Empty (the default) uses the resolvers of
    /// the operating system's network interfaces, or <see cref="FallbackResolvers"/> when it lists none. A resolver is asked for
    /// NS, A and AAAA records only; the TXT records themselves are never asked of it.
    /// </summary>
    public IReadOnlyList<IPEndPoint> Resolvers { get; init; } = [];

    /// <summary>Used when <see cref="Resolvers"/> is empty and the system names none: Cloudflare's and Google's public resolvers.</summary>
    public IReadOnlyList<IPEndPoint> FallbackResolvers { get; init; } =
        [new(IPAddress.Parse("1.1.1.1"), 53), new(IPAddress.Parse("8.8.8.8"), 53)];

    /// <summary>The port of the authoritative servers. 53, except in tests.</summary>
    public int AuthoritativePort { get; init; } = 53;
}
