using System.Globalization;

using Paduraksa.Proxy.Logging;

namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// One host's traffic over one stretch of time (a five-minute bucket as it is filled, an hour, or a whole range as it is read): the
/// counts, the distinct visitors, the top paths, referers, agents and largest responses, and who the clients were (PLAN §7.4,
/// analytics). Fixed memory by construction: every table has a cap, and the overflow is counted together, never grown. Built and
/// read on one thread (the drain's, or a reader's own copy); <see cref="MergeFrom"/> is what makes a range out of its buckets.
/// </summary>
public sealed class TrafficBucket
{
    /// <summary>Statuses kept apart, by exact code.</summary>
    public const int StatusCapacity = 64;

    public const int MethodCapacity = 16;
    public const int BrowserCapacity = 64;
    public const int OsCapacity = 32;
    public const int BotNameCapacity = 32;

    /// <summary>The longest path, referer host or user agent kept, in characters.</summary>
    public const int MaxKeyLength = 256;

    // The table sizes of a five-minute bucket; a bucket that is a merge of many (an hour, a range) is built with a larger scale.
    private const int PathCapacity = 64;
    private const int RefererCapacity = 32;
    private const int AgentCapacity = 32;
    private const int LargestCapacity = 16;

    private static readonly string[] StatusText = StatusTexts();

    // The Proxy's own rule: no LINQ, even off the request path, so there is one habit to keep.
    private static string[] StatusTexts()
    {
        var texts = new string[600];
        for (var code = 0; code < texts.Length; code++)
        {
            texts[code] = code.ToString(CultureInfo.InvariantCulture);
        }

        // A dropped connection sent no status: it is counted as "none", the API's name for that class.
        texts[0] = "none";

        return texts;
    }

    public TrafficBucket(int scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        Scale = scale;
        Statuses = new BoundedCounter(StatusCapacity);
        Methods = new BoundedCounter(MethodCapacity);
        Browsers = new BoundedCounter(BrowserCapacity * scale);
        OperatingSystems = new BoundedCounter(OsCapacity * scale);
        BotNames = new BoundedCounter(BotNameCapacity * scale);
        IgnoredRobots = new BoundedCounter(BotNameCapacity * scale);
        Visitors = new HyperLogLog();
        Paths = new HeavyHitters(PathCapacity * scale, keepsLatency: true);
        Referers = new HeavyHitters(RefererCapacity * scale);
        UserAgents = new HeavyHitters(AgentCapacity * scale);
        Largest = new HeavyHitters(LargestCapacity * scale);
    }

    internal TrafficBucket(
        long requests, long bytesOut, long serverErrors, long[] statusClasses, BoundedCounter statuses, BoundedCounter methods, long[] protocols, long[] devices,
        long[] botClasses, BoundedCounter browsers, BoundedCounter operatingSystems, BoundedCounter botNames, BoundedCounter ignoredRobots, HyperLogLog visitors,
        HeavyHitters paths, HeavyHitters referers, HeavyHitters userAgents, HeavyHitters largest)
    {
        Requests = requests;
        BytesOut = bytesOut;
        ServerErrors = serverErrors;
        StatusClasses = statusClasses;
        Statuses = statuses;
        Methods = methods;
        Protocols = protocols;
        Devices = devices;
        BotClasses = botClasses;
        Browsers = browsers;
        OperatingSystems = operatingSystems;
        BotNames = botNames;
        IgnoredRobots = ignoredRobots;
        Visitors = visitors;
        Paths = paths;
        Referers = referers;
        UserAgents = userAgents;
        Largest = largest;
        Scale = Math.Max(1, paths.Capacity / PathCapacity);
    }

    /// <summary>How many times the five-minute table sizes this bucket holds.</summary>
    public int Scale { get; }

    public long Requests { get; private set; }

    /// <summary>The bytes of the responses that declared a <c>Content-Length</c>; a streamed one adds nothing.</summary>
    public long BytesOut { get; private set; }

    /// <summary>Answers with a 5xx status.</summary>
    public long ServerErrors { get; private set; }

    /// <summary>Requests by status class: index 0 is no response (the connection was dropped), 1 to 5 the class.</summary>
    public long[] StatusClasses { get; } = new long[6];

    public BoundedCounter Statuses { get; }

    public BoundedCounter Methods { get; }

    /// <summary>Requests by protocol: <see cref="ProtocolOther"/>, then HTTP/1.0, HTTP/1.1, HTTP/2 and HTTP/3 at the indexes <c>Protocol*</c> name.</summary>
    public long[] Protocols { get; } = new long[5];

    public const int ProtocolOther = 0;
    public const int ProtocolHttp10 = 1;
    public const int ProtocolHttp11 = 2;
    public const int ProtocolHttp2 = 3;
    public const int ProtocolHttp3 = 4;

    /// <summary>Requests by <see cref="DeviceClass"/> (the enum's value is the index).</summary>
    public long[] Devices { get; } = new long[5];

    /// <summary>Requests by <see cref="BotClass"/> (the enum's value is the index; index 0, <see cref="BotClass.None"/>, is everything that wasn't a bot).</summary>
    public long[] BotClasses { get; } = new long[8];

    /// <summary>Browser family and major version of the clients that weren't bots (<c>Chrome 131</c>).</summary>
    public BoundedCounter Browsers { get; }

    public BoundedCounter OperatingSystems { get; }

    /// <summary>Requests of each named bot, by <see cref="UserAgentInfo.BotKey"/> (its kind and name).</summary>
    public BoundedCounter BotNames { get; }

    /// <summary>Requests of each named bot (by <see cref="UserAgentInfo.BotKey"/>) to a path the host's <c>robots.txt</c> disallows for it.</summary>
    public BoundedCounter IgnoredRobots { get; }

    public HyperLogLog Visitors { get; }

    public HeavyHitters Paths { get; }

    /// <summary>Where requests came from: <c>direct</c>, <c>(same site)</c> or the referer's scheme and host.</summary>
    public HeavyHitters Referers { get; }

    public HeavyHitters UserAgents { get; }

    /// <summary>Paths ranked by the bytes of their responses.</summary>
    public HeavyHitters Largest { get; }

    /// <summary>
    /// Counts one request. <paramref name="visitor"/> is the salted hash of the client address (0 when there was none),
    /// <paramref name="agent"/> the classified agent and <paramref name="ignoredRobots"/> whether a bot fetched what the host's
    /// <c>robots.txt</c> disallows for it.
    /// </summary>
    public void Record(in AccessLogEntry entry, ulong visitor, UserAgentInfo agent, bool ignoredRobots)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Requests++;
        var status = entry.StatusCode;
        var serverError = status >= 500;
        if (serverError)
        {
            ServerErrors++;
        }

        StatusClasses[status is >= 100 and < 600 ? status / 100 : 0]++;
        Statuses.Add(status is >= 0 and < 600 ? StatusText[status] : "other");
        Methods.Add(AnalyticsText.Clean(entry.Method, 16));
        Protocols[ProtocolOf(entry.Protocol)]++;

        var bytes = entry.ResponseBytes;
        if (bytes > 0)
        {
            BytesOut += bytes;
        }

        if (visitor != 0)
        {
            Visitors.Add(visitor);
        }

        var path = AnalyticsText.Clean(entry.Path, MaxKeyLength, "/");
        Paths.Add(path, 1, bytes, serverError, entry.TotalTicks / TimeSpan.TicksPerMicrosecond);
        if (bytes > 0)
        {
            Largest.Add(path, bytes, bytes, serverError, -1);
        }

        Referers.Add(RefererLabel(entry.Referer, entry.Host), 1, -1, false, -1);
        UserAgents.Add(AnalyticsText.Clean(entry.UserAgent, MaxKeyLength, "(none)"), 1, -1, false, -1);

        Devices[(int)agent.Device]++;
        BotClasses[(int)agent.Bot]++;
        if (agent.IsBot)
        {
            BotNames.Add(agent.BotKey ?? "Unknown|Unnamed bot");
            if (ignoredRobots)
            {
                IgnoredRobots.Add(agent.BotKey ?? "Unknown|Unnamed bot");
            }
        }
        else
        {
            Browsers.Add(agent.BrowserLabel);
            OperatingSystems.Add(agent.Os);
        }
    }

    /// <summary>Adds everything <paramref name="other"/> counted; the sketches keep their bounds (see <see cref="HeavyHitters"/>).</summary>
    public void MergeFrom(TrafficBucket other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Requests += other.Requests;
        BytesOut += other.BytesOut;
        ServerErrors += other.ServerErrors;
        Add(StatusClasses, other.StatusClasses);
        Add(Protocols, other.Protocols);
        Add(Devices, other.Devices);
        Add(BotClasses, other.BotClasses);
        Statuses.MergeFrom(other.Statuses);
        Methods.MergeFrom(other.Methods);
        Browsers.MergeFrom(other.Browsers);
        OperatingSystems.MergeFrom(other.OperatingSystems);
        BotNames.MergeFrom(other.BotNames);
        IgnoredRobots.MergeFrom(other.IgnoredRobots);
        Visitors.Merge(other.Visitors);
        Paths.MergeFrom(other.Paths);
        Referers.MergeFrom(other.Referers);
        UserAgents.MergeFrom(other.UserAgents);
        Largest.MergeFrom(other.Largest);
    }

    /// <summary>A copy that shares nothing with this bucket.</summary>
    public TrafficBucket Clone()
    {
        TrafficBucket copy = new(Scale);
        copy.MergeFrom(this);
        return copy;
    }

    private static void Add(long[] into, long[] from)
    {
        for (var i = 0; i < into.Length && i < from.Length; i++)
        {
            into[i] += from[i];
        }
    }

    private static int ProtocolOf(string? protocol) => protocol switch
    {
        "HTTP/1.1" => ProtocolHttp11,
        "HTTP/2" or "HTTP/2.0" => ProtocolHttp2,
        "HTTP/3" or "HTTP/3.0" => ProtocolHttp3,
        "HTTP/1.0" => ProtocolHttp10,
        _ => ProtocolOther,
    };

    /// <summary>
    /// The referer as a source label: scheme and host only (no path, query, port or credentials, which can carry a token),
    /// <c>direct</c> when there is none and <c>(same site)</c> when it is the host the request was for.
    /// </summary>
    internal static string RefererLabel(string? referer, string? requestHost)
    {
        if (string.IsNullOrEmpty(referer))
        {
            return "direct";
        }

        var span = referer.AsSpan();
        var scheme = span.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 1 || scheme > 16)
        {
            return "(other)";
        }

        var rest = span[(scheme + 3)..];
        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        var host = authority;
        if (host.Length > 0 && host[0] == '[')
        {
            var close = host.IndexOf(']');
            host = close < 0 ? host : host[..(close + 1)];
        }
        else
        {
            var colon = host.IndexOf(':');
            host = colon < 0 ? host : host[..colon];
        }

        if (host.IsEmpty || host.Length > 253)
        {
            return "(other)";
        }

        if (!string.IsNullOrEmpty(requestHost) && host.Equals(Hostname(requestHost), StringComparison.OrdinalIgnoreCase))
        {
            return "(same site)";
        }

        return string.Concat(span[..scheme].ToString().ToLowerInvariant(), "://", host.ToString().ToLowerInvariant());
    }

    private static ReadOnlySpan<char> Hostname(string hostHeader)
    {
        var host = hostHeader.AsSpan();
        if (host.Length > 0 && host[0] == '[')
        {
            var close = host.IndexOf(']');
            return close < 0 ? host : host[..(close + 1)];
        }

        var colon = host.IndexOf(':');
        return colon < 0 ? host : host[..colon];
    }
}
