using System.Threading.Channels;

using Paduraksa.Core.Model;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// The trap's sink (docs/design/waf.md "Honeypot", "Recording"): hands each hit to a bounded queue and returns. <see cref="FoldAsync"/> folds the queue
/// into sessions as hits arrive, and <see cref="Drain"/>, called by the recording job before each write, folds what is left and readies the rows. A
/// request never waits on the database, a lock or an allocation beyond the hit's own strings.
/// </summary>
/// <remarks>
/// <para><b>Counts stay whole under a flood.</b> The queue is folded continuously, and folding a hit is a dictionary update, far cheaper than serving
/// the trap page that produced it, so the queue only fills if the folding thread is starved. Only request rows are bounded by what the database
/// keeps up with, and by <see cref="MaxStoredRequestsPerSession"/>: a session's counters include every hit that wasn't dropped at the queue.</para>
/// <para><b>Overload loses hits, not time.</b> A full queue drops the hit and counts it (<see cref="HoneypotStats.CountDropped"/>); a robots.txt fetch that
/// finds it full is lost without a count, because it only decides one flag of a session. The rows waiting for a database that is down are
/// bounded too: the oldest go first, and the lost requests are counted as dropped.</para>
/// <para><b>Memory is bounded by what a hostile client can reach.</b> Active sessions, the pages each one remembers for its depth, the robots.txt
/// fetches remembered and the rows waiting to be written all have a cap. At a cap the least recently seen session ends early (a later request
/// from it starts a new one), a session stops remembering new pages (their children count one link from something unseen), and the oldest
/// robots.txt fetch is forgotten.</para>
/// <para><b>Sessions.</b> Keyed by host, full client address and user agent (cut to <see cref="HoneypotLimits.RecordedUserAgentLength"/>). A request that finds the session
/// idle for <see cref="IdleTimeout"/> or more begins a new one, so a visitor that comes back a day later is a second session. A restart ends every session.</para>
/// </remarks>
public sealed class HoneypotRecorder : IHoneypotSink
{
    /// <summary>How long a session may be idle before the next request starts a new one.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How long a robots.txt fetch counts as having come first for a session that begins after it.</summary>
    public static readonly TimeSpan RobotsMemory = TimeSpan.FromHours(24);

    // Room for a GC pause or a scheduling gap of the folding thread at the trap's full rate (tens of thousands of pages a second).
    public const int DefaultQueueCapacity = 65_536;

    // Hits folded per hold of the lock, so a write's Take never waits behind a long fold.
    private const int FoldChunk = 4_096;
    public const int MaxActiveSessions = 10_000;
    public const int MaxRememberedRobotsFetches = 50_000;
    public const int MaxPagesPerSession = 256;
    public const int MaxWaitingRequests = 200_000;
    public const int MaxWaitingSessions = 100_000;

    /// <summary>Request rows kept per session. A session's counters go on past it; its timeline holds the first this many.</summary>
    public const int MaxStoredRequestsPerSession = 10_000;

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
            // Two readers: the folding loop and the job's drain before a write (both read under _gate).
            SingleReader = false,
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
            while (FoldQueued())
            {
            }

            Snapshot();
            EndIdle(_time.GetUtcNow());
            EnforceSessionCaps();
        }
    }

    /// <summary>
    /// Folds hits into the sessions as they arrive, until <paramref name="cancellationToken"/> ends it. The recording job runs it beside its writes, so
    /// the queue stays near empty however fast the trap answers.
    /// </summary>
    public async Task FoldAsync(CancellationToken cancellationToken)
    {
        while (await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bool more;
            do
            {
                lock (_gate)
                {
                    more = FoldQueued();
                }
            }
            while (more);
        }
    }

    // Under _gate. True when it stopped at FoldChunk with more queued.
    private bool FoldQueued()
    {
        for (var i = 0; i < FoldChunk; i++)
        {
            if (!_reader.TryRead(out var next))
            {
                return false;
            }

            if (next.Robots)
            {
                RememberRobots(next.Hit);
            }
            else
            {
                Apply(next.Hit);
            }
        }

        return true;
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
        SessionKey key = new(fetch.Host, fetch.ClientIp, Cut(fetch.UserAgent, HoneypotLimits.RecordedUserAgentLength)!);
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

        // The counters take every hit; the rows stop at a cap per session, so a flood costs a database write per request only for its first ones.
        if (session.StoredRequests < MaxStoredRequestsPerSession)
        {
            session.StoredRequests++;
            _waitingRequests.Add(new HoneypotRequestRecord(session.Id, HoneypotRequestId.New(), hit));
            TrimWaiting();
        }
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
        UserAgent = Cut(hit.UserAgent, HoneypotLimits.RecordedUserAgentLength)!,
        Canary = Cut(hit.Canary, HoneypotLimits.RecordedCanaryLength)!,
        Protocol = Cut(hit.Protocol, HoneypotLimits.RecordedProtocolLength)!,
        Accept = Cut(hit.Accept, HoneypotLimits.RecordedAcceptLength),
        AcceptLanguage = Cut(hit.AcceptLanguage, HoneypotLimits.RecordedAcceptLanguageLength),
        SecFetchSite = Cut(hit.SecFetchSite, HoneypotLimits.RecordedSecFetchLength),
        SecFetchMode = Cut(hit.SecFetchMode, HoneypotLimits.RecordedSecFetchLength),
        SecFetchDest = Cut(hit.SecFetchDest, HoneypotLimits.RecordedSecFetchLength),
        SecFetchUser = Cut(hit.SecFetchUser, HoneypotLimits.RecordedSecFetchLength),
        Referer = Cut(hit.Referer, HoneypotLimits.RecordedRefererLength),
        Path = Cut(hit.Path, HoneypotLimits.RecordedPathLength)!,
        PathTruncated = hit.PathTruncated || hit.Path.Length > HoneypotLimits.RecordedPathLength,
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

        /// <summary>Request rows this session has queued for the database, for <see cref="MaxStoredRequestsPerSession"/>.</summary>
        public int StoredRequests { get; set; }

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
