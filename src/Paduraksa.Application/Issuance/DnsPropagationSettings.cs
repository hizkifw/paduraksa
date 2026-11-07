using System.Net;

using Paduraksa.Acme.Dns;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// The operator's settings of the DNS propagation check, in a shape the configuration binder fills (plain properties, text for
/// addresses): bind it from <c>Paduraksa:Acme:DnsPropagation</c>, for example
/// <c>{ "Enabled": true, "Timeout": "00:05:00", "Resolvers": [ "10.0.0.2", "10.0.0.3:53" ] }</c>. <see cref="ToOptions"/> turns it
/// into the check's own options.
/// </summary>
public sealed class DnsPropagationSettings
{
    /// <summary>Whether to wait for the zone's name servers at all. Off keeps the fixed <see cref="AcmeIssuanceOptions.DnsPropagationDelay"/>, for a host that cannot send DNS to them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the check waits in all.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The pause before the second round of queries.</summary>
    public TimeSpan InitialInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest pause between rounds.</summary>
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long one query to one server may take.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Rounds without an authoritative answer after which a name server is left out.</summary>
    public int UnreachableRounds { get; set; } = 3;

    /// <summary>The recursive resolvers that find the name servers (<c>1.1.1.1</c>, <c>10.0.0.2:53</c>, <c>[2606:4700::1111]:53</c>); empty uses the system's.</summary>
    public List<string> Resolvers { get; set; } = [];

    /// <summary>The check's options.</summary>
    /// <exception cref="ArgumentException">A value is out of range, or a resolver is not an address.</exception>
    public DnsPropagationOptions ToOptions()
    {
        if (Timeout <= TimeSpan.Zero || InitialInterval <= TimeSpan.Zero || MaxInterval < InitialInterval || QueryTimeout <= TimeSpan.Zero || UnreachableRounds < 1)
        {
            throw new ArgumentException("DNS propagation: the timeout and intervals must be positive, the maximum interval not below the initial one, and at least one round must count.");
        }

        List<IPEndPoint> resolvers = [];
        foreach (string text in Resolvers)
        {
            if (!IPEndPoint.TryParse(text.Trim(), out IPEndPoint? endpoint))
            {
                throw new ArgumentException($"DNS propagation: '{text}' is not a resolver address.");
            }

            resolvers.Add(endpoint.Port == 0 ? new IPEndPoint(endpoint.Address, 53) : endpoint);
        }

        return new DnsPropagationOptions
        {
            Timeout = Timeout,
            InitialInterval = InitialInterval,
            MaxInterval = MaxInterval,
            QueryTimeout = QueryTimeout,
            UnreachableRounds = UnreachableRounds,
            Resolvers = resolvers,
        };
    }
}
