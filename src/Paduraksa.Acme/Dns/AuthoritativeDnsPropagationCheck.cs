using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Paduraksa.Acme.Dns;

/// <summary>
/// <see cref="IDnsPropagationCheck"/> that asks the zone's own name servers. A record counts as propagated when every
/// authoritative server returns it, which is what a CA's resolvers will find once their caches hold nothing older; a
/// recursive resolver would only tell what <i>it</i> has cached, including a stale "no such record".
/// </summary>
/// <remarks>
/// A server counts as done when its answer is authoritative (AA bit) and NOERROR or NXDOMAIN, and holds every expected
/// value (a wildcard and its base name publish two values at one name); a name server with several addresses is done when
/// any one of them answers. The first round runs at once, then the pause grows from
/// <see cref="DnsPropagationOptions.InitialInterval"/> by <see cref="DnsPropagationOptions.BackoffFactor"/> to
/// <see cref="DnsPropagationOptions.MaxInterval"/>, until <see cref="DnsPropagationOptions.Timeout"/>. A server silent for
/// <see cref="DnsPropagationOptions.UnreachableRounds"/> rounds in a row is left out; if none is left, or the zone or the
/// resolvers cannot be found, the outcome is <see cref="DnsPropagationOutcome.Unverifiable"/> and the caller waits a fixed
/// delay instead.
/// </remarks>
public sealed partial class AuthoritativeDnsPropagationCheck : IDnsPropagationCheck
{
    private readonly DnsPropagationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly DnsClient _client;

    /// <param name="options">Tunables; the defaults when null.</param>
    /// <param name="time">The clock for the pauses and the timeout; the system clock when null.</param>
    /// <param name="logger">Where it logs; nowhere when null.</param>
    public AuthoritativeDnsPropagationCheck(DnsPropagationOptions? options = null, TimeProvider? time = null, ILogger<AuthoritativeDnsPropagationCheck>? logger = null)
    {
        _options = options ?? new DnsPropagationOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AuthoritativeDnsPropagationCheck>.Instance;
        _client = new DnsClient(_options.QueryTimeout);
    }

    /// <inheritdoc />
    public async ValueTask<DnsPropagationResult> WaitAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        List<DnsChallengeRecord> expected = [.. records.Select(r => new DnsChallengeRecord(DnsNames.Normalize(r.Name), DnsNames.RequireTxtValue(r.Value))).Distinct()];
        if (expected.Count == 0)
        {
            return new DnsPropagationResult(DnsPropagationOutcome.Propagated, "There was nothing to look for.");
        }

        IReadOnlyList<IPEndPoint> resolvers = Resolvers();
        long started = _time.GetTimestamp();
        List<string> unplaced = [.. expected.Select(r => r.Name).Distinct(StringComparer.Ordinal)];
        List<Zone> zones = [];
        TimeSpan pause = _options.InitialInterval;
        int discoveryFailures = 0;
        for (int round = 1; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int i = unplaced.Count - 1; i >= 0; i--)
            {
                if (await FindZoneAsync(unplaced[i], resolvers, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    Zone zone = zones.Find(z => z.Apex == found.Apex) ?? found;
                    if (!zones.Contains(zone))
                    {
                        zones.Add(zone);
                    }

                    zone.Records.AddRange(expected.Where(r => r.Name == unplaced[i]));

                    // A server that returned everything known so far has not been asked about these records yet.
                    zone.Hosts.ForEach(h => h.Satisfied = false);
                    unplaced.RemoveAt(i);
                }
            }

            if (unplaced.Count > 0 && ++discoveryFailures >= _options.UnreachableRounds)
            {
                string detail = $"The name servers of {string.Join(", ", unplaced)} could not be found through {string.Join(", ", resolvers)}.";
                LogUnverifiable(detail);
                return new DnsPropagationResult(DnsPropagationOutcome.Unverifiable, detail);
            }

            await Task.WhenAll(zones.SelectMany(z => z.Hosts.Where(h => !h.Satisfied && !h.Excluded(_options)).Select(h => CheckHostAsync(z, h, resolvers, cancellationToken)))).ConfigureAwait(false);

            if (unplaced.Count == 0)
            {
                if (zones.Any(z => z.Hosts.All(h => h.Excluded(_options))))
                {
                    string detail = $"No authoritative name server of {string.Join(", ", zones.Where(z => z.Hosts.All(h => h.Excluded(_options))).Select(z => z.Apex))} answered.";
                    LogUnverifiable(detail);
                    return new DnsPropagationResult(DnsPropagationOutcome.Unverifiable, detail);
                }

                if (zones.All(z => z.Hosts.All(h => h.Satisfied || h.Excluded(_options))))
                {
                    int skipped = zones.Sum(z => z.Hosts.Count(h => !h.Satisfied));
                    string detail = $"{zones.Sum(z => z.Hosts.Count(h => h.Satisfied))} name server(s) return the record(s) after {round} round(s)"
                        + (skipped > 0 ? $"; {skipped} gave no authoritative answer and were skipped." : ".");
                    LogPropagated(detail);
                    return new DnsPropagationResult(DnsPropagationOutcome.Propagated, detail);
                }
            }

            TimeSpan remaining = _options.Timeout - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                string detail = unplaced.Count > 0
                    ? $"The name servers of {string.Join(", ", unplaced)} could not be found in time."
                    : $"After {round} round(s) these name servers still lack the record(s): {string.Join(", ", zones.SelectMany(z => z.Hosts.Where(h => !h.Satisfied && !h.Excluded(_options))).Select(h => h.Name))}.";
                LogTimedOut(detail);
                return new DnsPropagationResult(DnsPropagationOutcome.TimedOut, detail, zones.Any(z => z.Hosts.Any(h => h.SeenRecords)));
            }

            await Task.Delay(pause < remaining ? pause : remaining, _time, cancellationToken).ConfigureAwait(false);
            pause = TimeSpan.FromTicks(Math.Min(_options.MaxInterval.Ticks, (long)(pause.Ticks * _options.BackoffFactor)));
        }
    }

    private static bool Holds(DnsResponse response, string name, string value) =>
        response.Answers.Any(a => a.Type == (ushort)DnsRecordType.TXT && a.Text == value && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool Usable(DnsResponse response) => response.ResponseCode is 0 or 3;

    private IReadOnlyList<IPEndPoint> Resolvers()
    {
        if (_options.Resolvers.Count > 0)
        {
            return _options.Resolvers;
        }

        List<IPEndPoint> system = [];
        try
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (IPAddress address in network.GetIPProperties().DnsAddresses)
                {
                    // fec0::/10 site-local addresses are a Windows placeholder for "no IPv6 resolver", not a server.
                    if (!address.IsIPv6SiteLocal && !system.Any(e => e.Address.Equals(address)))
                    {
                        system.Add(new IPEndPoint(address, 53));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No list of interfaces: the fallback below.
        }

        return system.Count > 0 ? system : _options.FallbackResolvers;
    }

    /// <summary>The first resolver that gives a usable answer to a recursive query, or null when none does.</summary>
    private async Task<DnsResponse?> AskResolversAsync(IReadOnlyList<IPEndPoint> resolvers, string name, DnsRecordType type, CancellationToken cancellationToken)
    {
        foreach (IPEndPoint resolver in resolvers)
        {
            try
            {
                DnsResponse response = await _client.QueryAsync(resolver, name, type, recursionDesired: true, cancellationToken).ConfigureAwait(false);
                if (Usable(response))
                {
                    return response;
                }
            }
            catch (DnsQueryException ex)
            {
                LogResolverFailed(resolver, name, ex.Message);
            }
        }

        return null;
    }

    /// <summary>The zone that holds <paramref name="name"/> (the closest ancestor with NS records) and its name servers, or null when the resolvers cannot say.</summary>
    private async Task<Zone?> FindZoneAsync(string name, IReadOnlyList<IPEndPoint> resolvers, CancellationToken cancellationToken)
    {
        foreach (string candidate in DnsNames.ZoneCandidates(name))
        {
            DnsResponse? response = await AskResolversAsync(resolvers, candidate, DnsRecordType.NS, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return null;
            }

            List<string> servers = [.. response.Answers
                .Where(a => a.Type == (ushort)DnsRecordType.NS && a.Target is not null && string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Target!.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)];
            if (servers.Count > 0)
            {
                return new Zone(candidate, [.. servers.Select(s => new Host(s))]);
            }
        }

        return null;
    }

    private async Task CheckHostAsync(Zone zone, Host host, IReadOnlyList<IPEndPoint> resolvers, CancellationToken cancellationToken)
    {
        host.Addresses ??= await ResolveAsync(host.Name, resolvers, cancellationToken).ConfigureAwait(false);
        if (host.Addresses.Count == 0)
        {
            host.Addresses = null;
            host.SilentRounds++;
            return;
        }

        foreach (IPAddress address in host.Addresses)
        {
            IPEndPoint server = new(address, _options.AuthoritativePort);
            bool answered = true;
            bool complete = true;
            foreach (IGrouping<string, DnsChallengeRecord> group in zone.Records.GroupBy(r => r.Name, StringComparer.Ordinal))
            {
                try
                {
                    DnsResponse response = await _client.QueryAsync(server, group.Key, DnsRecordType.TXT, recursionDesired: false, cancellationToken).ConfigureAwait(false);
                    if (!response.Authoritative || !Usable(response))
                    {
                        // Lame: a server that does not claim the zone says nothing about it.
                        LogNotAuthoritative(host.Name, server);
                        answered = false;
                        break;
                    }

                    complete &= group.All(r => Holds(response, r.Name, r.Value));
                    host.SeenRecords |= group.Any(r => Holds(response, r.Name, r.Value));
                }
                catch (DnsQueryException ex)
                {
                    LogServerFailed(host.Name, server, ex.Message);
                    answered = false;
                    break;
                }
            }

            if (answered)
            {
                host.SilentRounds = 0;
                host.Satisfied = complete;
                return;
            }
        }

        host.SilentRounds++;
    }

    /// <summary>The addresses of a name server, IPv4 first; empty when the resolvers do not know it.</summary>
    private async Task<List<IPAddress>> ResolveAsync(string host, IReadOnlyList<IPEndPoint> resolvers, CancellationToken cancellationToken)
    {
        List<IPAddress> addresses = [];
        foreach (DnsRecordType type in (DnsRecordType[])[DnsRecordType.A, DnsRecordType.AAAA])
        {
            if (await AskResolversAsync(resolvers, host, type, cancellationToken).ConfigureAwait(false) is { } response)
            {
                addresses.AddRange(response.Answers
                    .Where(a => a.Address is not null && a.Address.AddressFamily == (type == DnsRecordType.A ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6))
                    .Select(a => a.Address!));
            }
        }

        return [.. addresses.Distinct()];
    }

    private sealed class Zone(string apex, List<Host> hosts)
    {
        public string Apex { get; } = apex;

        public List<Host> Hosts { get; } = hosts;

        public List<DnsChallengeRecord> Records { get; } = [];
    }

    private sealed class Host(string name)
    {
        public string Name { get; } = name;

        public List<IPAddress>? Addresses { get; set; }

        public int SilentRounds { get; set; }

        public bool Satisfied { get; set; }

        /// <summary>Whether this server has ever returned any of the records.</summary>
        public bool SeenRecords { get; set; }

        public bool Excluded(DnsPropagationOptions options) => !Satisfied && SilentRounds >= options.UnreachableRounds;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "DNS propagation: resolver {Resolver} did not answer for {Name}: {Reason}")]
    private partial void LogResolverFailed(IPEndPoint resolver, string name, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DNS propagation: name server {Host} ({Server}) gave no usable answer: {Reason}")]
    private partial void LogServerFailed(string host, IPEndPoint server, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DNS propagation: name server {Host} ({Server}) answered without authority.")]
    private partial void LogNotAuthoritative(string host, IPEndPoint server);

    [LoggerMessage(Level = LogLevel.Information, Message = "DNS propagation: {Detail}")]
    private partial void LogPropagated(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DNS propagation timed out: {Detail}")]
    private partial void LogTimedOut(string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DNS propagation cannot be checked: {Detail}")]
    private partial void LogUnverifiable(string detail);
}
