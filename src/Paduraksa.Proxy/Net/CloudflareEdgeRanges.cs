using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Paduraksa.Proxy.Net;

/// <summary>Where an <see cref="EdgeRangeList"/> came from.</summary>
public enum EdgeRangeOrigin : byte
{
    /// <summary>The list shipped with this build (<see cref="CloudflareIpRanges"/>).</summary>
    BuiltIn = 0,

    /// <summary>A list fetched from Cloudflare by the refresh job and stored.</summary>
    Fetched,

    /// <summary>A set handed to the compiler directly (<c>SnapshotCompilerOptions.CloudflareEdgeRanges</c>).</summary>
    Configured,
}

/// <summary>
/// One list of Cloudflare's edge ranges with the facts the dashboard and the simulator report about it. Immutable.
/// </summary>
public sealed class EdgeRangeList
{
    /// <summary>Entries a list needs per family to be believed: Cloudflare publishes 15 and 7.</summary>
    public const int MinimumPerFamily = 3;

    /// <summary>Upper bound on entries, so a hostile or broken response can't build a huge set.</summary>
    public const int MaximumEntries = 512;

    /// <summary>The shortest IPv4 prefix a list may contain. Anything wider covers more of the internet than Cloudflare.</summary>
    public const int ShortestIPv4Prefix = 8;

    /// <summary>The shortest IPv6 prefix a list may contain.</summary>
    public const int ShortestIPv6Prefix = 12;

    private EdgeRangeList(CidrSet ranges, ImmutableArray<string> blocks, EdgeRangeOrigin origin, DateTimeOffset fetchedAt)
    {
        Ranges = ranges;
        Blocks = blocks;
        Origin = origin;
        FetchedAt = fetchedAt;
    }

    /// <summary>The list this build ships; its time is the date it was fetched on.</summary>
    public static EdgeRangeList BuiltIn { get; } = new(
        CloudflareIpRanges.Default,
        CanonicalBlocks(CloudflareIpRanges.IPv4, CloudflareIpRanges.IPv6),
        EdgeRangeOrigin.BuiltIn,
        new DateTimeOffset(CloudflareIpRanges.FetchedOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    // The same canonical text, sorted, as a fetched list has, so a first fetch that matches this build's list compares equal.
    private static ImmutableArray<string> CanonicalBlocks(ImmutableArray<string> v4, ImmutableArray<string> v6)
    {
        SortedSet<string> blocks = new(StringComparer.Ordinal);
        foreach (var block in v4)
        {
            blocks.Add(IpNetwork.Parse(block).ToString());
        }

        foreach (var block in v6)
        {
            blocks.Add(IpNetwork.Parse(block).ToString());
        }

        return ImmutableArray.CreateRange(blocks);
    }

    public CidrSet Ranges { get; }

    /// <summary>The blocks as written (canonical text, sorted, no duplicates): what is stored and compared.</summary>
    public ImmutableArray<string> Blocks { get; }

    public EdgeRangeOrigin Origin { get; }

    /// <summary>When the list was fetched (UTC); meaningless for <see cref="EdgeRangeOrigin.Configured"/>.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>How many blocks the list has, before the set merges neighbours.</summary>
    public int Count => Blocks.Length;

    /// <summary>A set handed to the compiler directly, as the tests do: it has no blocks as written and no fetch time.</summary>
    public static EdgeRangeList FromSet(CidrSet ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return new EdgeRangeList(ranges, [], EdgeRangeOrigin.Configured, default);
    }

    /// <summary>
    /// A fetched list, or why it isn't one. Every entry must be a CIDR block the strict parser accepts, both families must
    /// be there with at least <see cref="MinimumPerFamily"/> blocks each, and no block may be wider than
    /// <see cref="ShortestIPv4Prefix"/> (IPv4) or <see cref="ShortestIPv6Prefix"/> (IPv6): a list that fails any of it is
    /// refused whole, because a trust list that is half right is worse than the old one.
    /// </summary>
    public static bool TryCreate(IEnumerable<string> blocks, DateTimeOffset fetchedAt, [NotNullWhen(true)] out EdgeRangeList? list, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        list = null;
        SortedSet<string> unique = new(StringComparer.Ordinal);
        CidrSet.Builder builder = new();
        int v4 = 0, v6 = 0;
        foreach (var raw in blocks)
        {
            var text = raw?.Trim() ?? string.Empty;
            IpNetwork network = default;

            // A bare address would parse as a /32: the published lists always carry a prefix, so its absence is a bad list.
            var error = text.Contains('/', StringComparison.Ordinal) ? null : "no prefix length";
            if (error is null)
            {
                _ = IpNetwork.TryParse(text, out network, out error);
            }

            if (error is not null)
            {
                reason = $"'{Shown(text)}' is not a valid CIDR block: {error}";
                return false;
            }

            var shortest = network.IsIPv4 ? ShortestIPv4Prefix : ShortestIPv6Prefix;
            if (network.PrefixLength < shortest)
            {
                reason = $"'{Shown(text)}' is wider than /{shortest}, which no Cloudflare edge range is";
                return false;
            }

            if (!unique.Add(network.ToString()))
            {
                continue;
            }

            if (network.IsIPv4)
            {
                v4++;
            }
            else
            {
                v6++;
            }

            builder.Add(network);
            if (unique.Count > MaximumEntries)
            {
                reason = $"more than {MaximumEntries} blocks";
                return false;
            }
        }

        if (v4 < MinimumPerFamily || v6 < MinimumPerFamily)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"{v4} IPv4 and {v6} IPv6 blocks; a real list has at least {MinimumPerFamily} of each");
            return false;
        }

        list = new EdgeRangeList(builder.Build(), ImmutableArray.CreateRange(unique), EdgeRangeOrigin.Fetched, fetchedAt);
        reason = null;
        return true;
    }

    /// <summary>The same blocks as <paramref name="other"/>: a refetch that found nothing new.</summary>
    public bool SameBlocksAs(EdgeRangeList other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Blocks.AsSpan().SequenceEqual(other.Blocks.AsSpan());
    }

    // A response is data from the network: cut what ends up in a log line or an error.
    private static string Shown(string text) => text.Length <= 64 ? text : string.Concat(text.AsSpan(0, 64), "...");
}
