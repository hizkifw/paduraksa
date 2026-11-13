using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Paduraksa.Acme.Dns;

/// <summary>
/// <see cref="IDnsChallengeNames"/> that reads the CNAME chain through the same recursive resolvers the propagation check uses. A name
/// that has a CNAME can hold no TXT record, so a CNAME at <c>_acme-challenge.name</c> can only mean delegation: it is followed
/// without a setting (turning the propagation check off turns this off with it, as both need to send DNS queries).
/// </summary>
/// <remarks>The chain is followed at most <see cref="MaxHops"/> names deep, as resolvers do (RFC 1034 leaves it open; CAs stop at about eight).</remarks>
public sealed partial class CnameDelegation : IDnsChallengeNames
{
    public const int MaxHops = 8;

    private readonly DnsPropagationOptions _options;
    private readonly DnsClient _client;
    private readonly ILogger _logger;

    public CnameDelegation(DnsPropagationOptions? options = null, ILogger<CnameDelegation>? logger = null)
    {
        _options = options ?? new DnsPropagationOptions();
        _client = new DnsClient(_options.QueryTimeout);
        _logger = logger ?? NullLogger<CnameDelegation>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<string> ResolveAsync(string recordName, CancellationToken cancellationToken)
    {
        var name = DnsNames.Normalize(recordName);
        var resolvers = DnsResolvers.For(_options);
        HashSet<string> seen = new(StringComparer.Ordinal) { name };
        var current = name;
        for (var hop = 0; ; hop++)
        {
            var next = await CnameOfAsync(current, resolvers, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                if (current != name)
                {
                    LogDelegated(name, current);
                }

                return current;
            }

            if (hop >= MaxHops || !seen.Add(next))
            {
                throw new DnsProviderException(
                    $"The CNAME chain at {name} " + (hop >= MaxHops ? $"is longer than {MaxHops} names" : $"loops back to {next}") + ", so no TXT record can make the challenge validate.", isTransient: false);
            }

            current = next;
        }
    }

    private async Task<string?> CnameOfAsync(string name, IReadOnlyList<System.Net.IPEndPoint> resolvers, CancellationToken cancellationToken)
    {
        foreach (var resolver in resolvers)
        {
            try
            {
                var response = await _client.QueryAsync(resolver, name, DnsRecordType.CNAME, recursionDesired: true, cancellationToken).ConfigureAwait(false);
                if (response.ResponseCode is not (0 or 3))
                {
                    continue;
                }

                foreach (var answer in response.Answers)
                {
                    if (answer.Type == (ushort)DnsRecordType.CNAME && answer.Target is { } target && string.Equals(answer.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return DnsNames.Normalize(target);
                    }
                }

                return null;
            }
            catch (DnsQueryException ex)
            {
                LogResolverFailed(resolver, name, ex.Message);
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "CNAME delegation: resolver {Resolver} did not answer for {Name}: {Reason}")]
    private partial void LogResolverFailed(System.Net.IPEndPoint resolver, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "DNS-01: {Name} is a CNAME; the challenge record goes to {Target}.")]
    private partial void LogDelegated(string name, string target);
}
