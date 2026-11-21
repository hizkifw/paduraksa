using System.Threading.Channels;

using Paduraksa.Core.Model;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// The trap's sink (docs/design/waf.md "Honeypot", "Recording"): hands each hit to a bounded queue and returns, and <see cref="Drain"/>, called by the
/// recording job, folds the queue into sessions and the rows waiting to be written. A request never waits on the database, a lock or an allocation
/// beyond the hit's own strings.
/// </summary>
/// <remarks>
/// <para><b>Overload loses hits, not time.</b> A full queue drops the hit and counts it (<see cref="HoneypotStats.CountDropped"/>); a robots.txt fetch that
/// finds it full is lost without a count, because it only decides one flag of a session. The rows waiting for a database that is down are
/// bounded too: the oldest go first, and the lost requests are counted as dropped.</para>
/// <para><b>Memory is bounded by what a hostile client can reach.</b> Active sessions, the pages each one remembers for its depth, the robots.txt
/// fetches remembered and the rows waiting to be written all have a cap. At a cap the least recently seen session ends early (a later request
/// from it starts a new one), a session stops remembering new pages (their children count one link from something unseen), and the oldest
/// robots.txt fetch is forgotten.</para>
/// <para><b>Sessions.</b> Keyed by host, full client address and user agent (cut to <see cref="UserAgentLength"/>). A request that finds the session
/// idle for <see cref="IdleTimeout"/> or more begins a new one, so a visitor that comes back a day later is a second session. A restart ends every session.</para>
/// </remarks>
public sealed class HoneypotRecorder : IHoneypotSink
{
    /// <summary>How long a session may be idle before the next request starts a new one.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How long a robots.txt fetch counts as having come first for a session that begins after it.</summary>
    public static readonly TimeSpan RobotsMemory = TimeSpan.FromHours(24);

    public const int DefaultQueueCapacity = 8_192;
    public const int MaxActiveSessions = 10_000;
    public const int MaxRememberedRobotsFetches = 50_000;
    public const int MaxPagesPerSession = 256;
    public const int MaxWaitingRequests = 200_000;
    public const int MaxWaitingSessions = 100_000;

    /// <summary>Cuts, in characters, of what a client sent: the same lengths the tables hold.</summary>
    public const int UserAgentLength = 512;
    public const int CanaryLength = 64;
    public const int AcceptLength = 256;
    public const int AcceptLanguageLength = 128;
    public const int SecFetchLength = 32;
    public const int RefererLength = 512;
    public const int ProtocolLength = 16;

    private static readonly int LinkKinds = Enum.GetValues<HoneypotLinkKind>().Length;

    private readonly HoneypotStats _stats;
    private readonly TimeProvider _time;
    private readonly ChannelReader<Event> _reader;
    private readonly ChannelWriter<Event> _writer;
    private readonly Lock _gate = new();

    // Everything below is touched only under _gate.
    private readonly Dictionary<SessionKey, Session> _sessions = [];
    private readonly Dictionary<SessionKey, DateTimeOffset> _robots = [];
    private readonly Queue<(SessionKey Key, DateTimeOffset At)> _robotsOrder = new();
    private readonly List<Session> _dirty = [];
    private readonly Dictionary<HoneypotSessionId, HoneypotSessionRecord> _waitingSessions = [];
    private List<HoneypotRequestRecord> _waitingRequests = [];

    public HoneypotRecorder(HoneypotStats stats, TimeProvider? time = null, int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _stats = stats;
        _time = time ?? TimeProvider.System;
        var queue = System.Threading.Channels.Channel.CreateBounded<Event>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        _reader = queue.Reader;
        _writer = queue.Writer;
    }

    /// <summary>Active sessions in memory.</summary>
    public int ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Rows waiting to be written: sessions and requests.</summary>
    public (int Sessions, int Requests) Waiting
    {
        get
        {
            lock (_gate)
            {
                return (_waitingSessions.Count, _waitingRequests.Count);
            }
        }
    }

    public void Record(in HoneypotHit hit)
    {
        if (!_writer.TryWrite(new Event(hit, Robots: false)))
        {
            _stats.CountDropped();
        }
    }

    public void RobotsFetched(HostId host, IpKey clientIp, string userAgent) =>
        _writer.TryWrite(new Event(
            new HoneypotHit(_time.GetUtcNow(), host, clientIp, userAgent, string.Empty, default, default, default, 0, 0, 0, default, 0, string.Empty, default, null, null, null, null, null, null, null),
            Robots: true));

    /// <summary>
    /// Folds everything queued into the sessions, ends the idle ones and leaves the changes waiting for <see cref="Take"/>. The recording job calls it
    /// before each write; it is public so tests drive it without the job.
    /// </summary>
    public void Drain()
    {
        lock (_gate)
        {
            while (_reader.TryRead(out var next))
            {
                if (next.Robots)
                {
                    RememberRobots(next.Hit);
                }
                else
                {
                    Apply(next.Hit);
                }
            }

            Snapshot();
            EndIdle(_time.GetUtcNow());
            EnforceSessionCaps();
        }
    }

    /// <summary>Hands over what waits to be written. A batch that couldn't be written goes back with <see cref="Requeue"/>.</summary>
    public HoneypotBatch Take()
    {
        lock (_gate)
        {
            var sessions = new HoneypotSessionRecord[_waitingSessions.Count];
            _waitingSessions.Values.CopyTo(sessions, 0);
            var requests = _waitingRequests;
            _waitingSessions.Clear();
            _waitingRequests = [];
            return new HoneypotBatch(sessions, requests);
        }
    }

    /// <summary>
    /// Puts back a batch whose write failed, ahead of what came in meanwhile. A session that changed since keeps its newer record; the cap on
    /// waiting rows drops the oldest.
    /// </summary>
    public void Requeue(HoneypotBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            foreach (var record in batch.Sessions)
            {
                _waitingSessions.TryAdd(record.Id, record);
            }

            if (batch.Requests.Count > 0)
            {
                List<HoneypotRequestRecord> merged = new(batch.Requests.Count + _waitingRequests.Count);
                merged.AddRange(batch.Requests);
                merged.AddRange(_waitingRequests);
                _waitingRequests = merged;
            }

            TrimWaiting();
        }
    }

    private void RememberRobots(HoneypotHit fetch)
    {
        SessionKey key = new(fetch.Host, fetch.ClientIp, Cut(fetch.UserAgent, UserAgentLength)!);
        _robots[key] = fetch.Time;
        _robotsOrder.Enqueue((key, fetch.Time));
        while (_robotsOrder.Count > 0 && (_robotsOrder.Count > MaxRememberedRobotsFetches || fetch.Time - _robotsOrder.Peek().At > RobotsMemory))
        {
            var (oldKey, at) = _robotsOrder.Dequeue();
            // A visitor that fetched again has a newer entry that outlives this one.
            if (_robots.TryGetValue(oldKey, out var current) && current == at)
            {
                _robots.Remove(oldKey);
            }
        }
    }

    private void Apply(HoneypotHit raw)
    {
        var hit = Trimmed(raw);
        SessionKey key = new(hit.Host, hit.ClientIp, hit.UserAgent);
        if (!_sessions.TryGetValue(key, out var session) || hit.Time - session.LastSeen >= IdleTimeout)
        {
            var readRobots = _robots.TryGetValue(key, out var fetched) && fetched <= hit.Time && hit.Time - fetched <= RobotsMemory;
            session = new Session(HoneypotSessionId.New(), hit, readRobots);
            _sessions[key] = session;
        }

        session.Apply(hit);
        if (!session.Dirty)
        {
            session.Dirty = true;
            _dirty.Add(session);
        }

        _waitingRequests.Add(new HoneypotRequestRecord(session.Id, HoneypotRequestId.New(), hit));
        TrimWaiting();
    }

    // A record of every session that changed, once per drain: the counters are running totals, so the newest record of a session is all a write needs.
    private void Snapshot()
    {
        foreach (var session in _dirty)
        {
            session.Dirty = false;
            _waitingSessions[session.Id] = session.ToRecord();
        }

        _dirty.Clear();
        TrimWaiting();
    }

    private void EndIdle(DateTimeOffset now)
    {
        List<SessionKey>? ended = null;
        foreach (var (key, session) in _sessions)
        {
            if (now - session.LastSeen >= IdleTimeout)
            {
                (ended ??= []).Add(key);
            }
        }

        if (ended is not null)
        {
            foreach (var key in ended)
            {
                _sessions.Remove(key);
            }
        }
    }

    private void EnforceSessionCaps()
    {
        if (_sessions.Count <= MaxActiveSessions)
        {
            return;
        }

        // An eighth at once, so a flood of new sessions doesn't sort the table on every drain.
        var keep = MaxActiveSessions - (MaxActiveSessions / 8);
        var all = new KeyValuePair<SessionKey, Session>[_sessions.Count];
        ((ICollection<KeyValuePair<SessionKey, Session>>)_sessions).CopyTo(all, 0);
        Array.Sort(all, static (a, b) => b.Value.LastSeen.CompareTo(a.Value.LastSeen));
        for (var i = keep; i < all.Length; i++)
        {
            _sessions.Remove(all[i].Key);
        }
    }

    private void TrimWaiting()
    {
        if (_waitingRequests.Count > MaxWaitingRequests)
        {
            var drop = _waitingRequests.Count - MaxWaitingRequests + (MaxWaitingRequests / 16);
            _waitingRequests.RemoveRange(0, drop);
            for (var i = 0; i < drop; i++)
            {
                _stats.CountDropped();
            }
        }

        if (_waitingSessions.Count > MaxWaitingSessions)
        {
            var records = new HoneypotSessionRecord[_waitingSessions.Count];
            _waitingSessions.Values.CopyTo(records, 0);
            Array.Sort(records, static (a, b) => a.StartedAt.CompareTo(b.StartedAt));
            var drop = records.Length - MaxWaitingSessions + (MaxWaitingSessions / 16);
            HashSet<HoneypotSessionId> dropped = new(drop);
            for (var i = 0; i < drop; i++)
            {
                _waitingSessions.Remove(records[i].Id);
                dropped.Add(records[i].Id);
            }

            // Their requests go too: requests name their session without a foreign key, so rows written without one would
            // be shown by no endpoint and still count against the request cap.
            var removed = _waitingRequests.RemoveAll(r => dropped.Contains(r.SessionId));
            for (var i = 0; i < removed; i++)
            {
                _stats.CountDropped();
            }
        }
    }

    private static HoneypotHit Trimmed(HoneypotHit hit) => hit with
    {
        UserAgent = Cut(hit.UserAgent, UserAgentLength)!,
        Canary = Cut(hit.Canary, CanaryLength)!,
        Protocol = Cut(hit.Protocol, ProtocolLength)!,
        Accept = Cut(hit.Accept, AcceptLength),
        AcceptLanguage = Cut(hit.AcceptLanguage, AcceptLanguageLength),
        SecFetchSite = Cut(hit.SecFetchSite, SecFetchLength),
        SecFetchMode = Cut(hit.SecFetchMode, SecFetchLength),
        SecFetchDest = Cut(hit.SecFetchDest, SecFetchLength),
        SecFetchUser = Cut(hit.SecFetchUser, SecFetchLength),
        Referer = Cut(hit.Referer, RefererLength),
        Path = Cut(hit.Path, HoneypotHit.MaxPathLength)!,
        PathTruncated = hit.PathTruncated || hit.Path.Length > HoneypotHit.MaxPathLength,
    };

    private static string? Cut(string? value, int length) => value is not null && value.Length > length ? value[..length] : value;

    private readonly record struct Event(HoneypotHit Hit, bool Robots);

    private readonly record struct SessionKey(HostId Host, IpKey ClientIp, string UserAgent);

    private sealed class Session
    {
        private readonly int[] _links = new int[LinkKinds];

        // Pages this session was served and how many links from the index each is. Allocated with the first page, and never grown past MaxPagesPerSession.
        private Dictionary<ulong, int>? _depths;
        private string _canary = string.Empty;

        public Session(HoneypotSessionId id, HoneypotHit first, bool readRobotsFirst)
        {
            Id = id;
            Host = first.Host;
            ClientIp = first.ClientIp;
            UserAgent = first.UserAgent;
            StartedAt = first.Time;
            LastSeen = first.Time;
            ReadRobotsFirst = readRobotsFirst;
        }

        public bool Dirty { get; set; }

        public DateTimeOffset LastSeen { get; private set; }

        public HoneypotSessionId Id { get; }

        private HostId Host { get; }

        private IpKey ClientIp { get; }

        private string UserAgent { get; }

        private DateTimeOffset StartedAt { get; }

        private bool ReadRobotsFirst { get; }

        private int Requests { get; set; }

        private int Pages { get; set; }

        private int GuessedUrls { get; set; }

        private int Images { get; set; }

        private int JsRuns { get; set; }

        private int ChallengesShown { get; set; }

        private int ChallengesSolved { get; set; }

        private int ChallengesPassed { get; set; }

        private int MaxDepth { get; set; }

        public void Apply(HoneypotHit hit)
        {
            Requests++;
            if (hit.Time > LastSeen)
            {
                LastSeen = hit.Time;
            }

            if (_canary.Length == 0)
            {
                _canary = hit.Canary;
            }

            switch (hit.Pow)
            {
                case HoneypotPowOutcome.Shown:
                    ChallengesShown++;
                    break;
                case HoneypotPowOutcome.Solved:
                    // The verify request carries the page the visitor goes back to, but it is no link followed and no page seen.
                    ChallengesSolved++;
                    return;
                case HoneypotPowOutcome.Passed:
                    ChallengesPassed++;
                    break;
            }

            _links[(int)hit.LinkKind]++;
            switch (hit.PageKind)
            {
                case HoneypotPageKind.NotFound:
                    GuessedUrls++;
                    break;
                case HoneypotPageKind.Image:
                    Images++;
                    break;
                case HoneypotPageKind.Sitemap or HoneypotPageKind.Feed or HoneypotPageKind.Beacon:
                    break;
                default:
                    Pages++;
                    if (hit.Pow != HoneypotPowOutcome.Shown)
                    {
                        Reach(hit);
                    }

                    break;
            }

            if (hit.PageKind == HoneypotPageKind.Beacon || hit.LinkKind is HoneypotLinkKind.Script or HoneypotLinkKind.Beacon)
            {
                JsRuns++;
            }
        }

        private void Reach(HoneypotHit hit)
        {
            _depths ??= [];
            if (!_depths.TryGetValue(hit.PageId, out var depth))
            {
                depth = hit.PageKind == HoneypotPageKind.Index ? 0 : _depths.TryGetValue(hit.ParentId, out var parent) ? parent + 1 : 1;
                if (_depths.Count < MaxPagesPerSession)
                {
                    _depths[hit.PageId] = depth;
                }
            }

            if (depth > MaxDepth)
            {
                MaxDepth = depth;
            }
        }

        public HoneypotSessionRecord ToRecord() => new(
            Id, Host, ClientIp, UserAgent, _canary, StartedAt, LastSeen, ReadRobotsFirst,
            Requests, Pages, GuessedUrls, Images, JsRuns, ChallengesShown, ChallengesSolved, ChallengesPassed, MaxDepth, (int[])_links.Clone());
    }
}
