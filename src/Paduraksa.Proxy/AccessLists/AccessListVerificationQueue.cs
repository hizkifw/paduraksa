using System.Security.Cryptography;
using System.Threading.Tasks.Sources;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.AccessLists;

/// <summary>How a request's wait for a verification slot ended.</summary>
internal enum VerificationSlot : byte
{
    /// <summary>The caller holds a slot and must give it back with <see cref="AccessListVerificationQueue.Exit"/>.</summary>
    Granted,

    /// <summary>The client already has its share of places (queued and running) and takes no more.</summary>
    ClientFull,

    /// <summary>The queue is at its length cap.</summary>
    QueueFull,

    /// <summary>The wait reached its maximum.</summary>
    TimedOut,

    /// <summary>The request was abandoned (the client went away) and left the queue.</summary>
    Cancelled,
}

/// <summary>
/// The bounded, fair line for PBKDF2 slots. A request that finds no free slot waits here, in arrival order, instead of being
/// refused: a burst of logins (or a spray) slows logins down rather than turning them away. Three bounds keep it from being
/// a way in for a flood: a cap on the line's length, a cap on the places one client (a masked address) may hold, queued and
/// running together, so a single source can only fill its own share of the line, and a maximum wait, after which the request is
/// refused. Two rules decide who runs next, so the line stays fair without a place being wasted: a client never runs more than
/// a few checks at once (its other requests wait, and others overtake them), and a request whose credentials are being checked
/// right now waits for that check, after which the cache answers it, instead of paying a second PBKDF2 for the same password.
/// </summary>
/// <remarks>
/// <para><b>Waiting costs no thread.</b> A waiter is a pooled <see cref="IValueTaskSource{TResult}"/>; the request's
/// continuation is queued to the thread pool when its turn comes (never run inline on the thread that frees the slot, which
/// would stack one PBKDF2 on another). A request that gets a slot at once, the common case, allocates nothing here.</para>
/// <para><b>Who is next.</b> A free slot goes to the first waiter in arrival order whose client is under its running cap and whose
/// key (the credentials' fingerprint, 0 for none) isn't running. Looking past a waiter that can't run costs a scan of a line at
/// most a few hundred long, under the lock; arrival order still decides every waiter's deadline, so the head is always the next to
/// expire.</para>
/// <para><b>One lock</b> covers the counts and the line. It is held for a few field writes and never while a waiter is
/// completed, so a continuation can't run under it. Only a PBKDF2 miss gets here, and a PBKDF2 costs tens of milliseconds.</para>
/// <para><b>The timer.</b> One timer serves the whole line. Arrivals wait the same maximum, so the head always has the
/// earliest deadline; the timer is armed for it when the line is first non-empty and, each time it fires, rearmed for the next
/// head. A waiter that leaves some other way (granted, cancelled) does not touch the timer: it fires, finds nothing due and
/// rearms or lapses.</para>
/// <para><b>Reuse.</b> A waiter goes back to the pool when its result is read, after its cancellation registration is
/// disposed (which waits for a callback running on another thread), so a late cancellation can't reach the next request that
/// takes it.</para>
/// </remarks>
internal sealed class AccessListVerificationQueue
{
    /// <summary>Stripes of the per-client counters (4 KiB): a client maps to one by a keyed hash of its masked address, so two
    /// clients share a stripe only by chance, and nobody can aim at another's.</summary>
    private const int ClientStripes = 1024;

    private const int MaxPooledWaiters = 64;

    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly AccessListStats _stats;
    private readonly int _slots;
    private readonly int _maxQueued;
    private readonly int _maxPlacesPerClient;
    private readonly int _maxRunningPerClient;
    private readonly long _maxWaitTicks;
    private readonly int[] _places = new int[ClientStripes];
    private readonly int[] _runningByClient = new int[ClientStripes];
    private readonly ulong[] _runningKeys;
    private readonly ulong _stripeSeed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    private readonly Stack<Waiter> _pool = new();
    private ITimer? _timer;
    private bool _timerPending;
    private int _running;
    private int _queued;
    private Waiter? _head;
    private Waiter? _tail;

    public AccessListVerificationQueue(TimeProvider time, AccessListStats stats, int slots, int maxQueued, int maxPlacesPerClient, int maxRunningPerClient, TimeSpan maxWait)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueued);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPlacesPerClient, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRunningPerClient, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWait, TimeSpan.Zero);
        _time = time;
        _stats = stats;
        _slots = slots;
        _maxQueued = maxQueued;
        _maxPlacesPerClient = maxPlacesPerClient;
        _maxRunningPerClient = maxRunningPerClient;
        _runningKeys = new ulong[slots];
        _maxWaitTicks = (long)(maxWait.TotalSeconds * time.TimestampFrequency);
    }

    public int Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    public int Queued
    {
        get
        {
            lock (_lock)
            {
                return _queued;
            }
        }
    }

    /// <summary>
    /// Waits for a slot for <paramref name="client"/>. <paramref name="key"/> names the credentials being checked (0: none), so
    /// two requests for the same ones never run at once. Synchronous (and allocation-free) when a slot is free and the request may
    /// run, or can't enter the line; otherwise it completes when a slot is handed over, the wait runs out or
    /// <paramref name="cancellation"/> fires. <see cref="VerificationSlot.Granted"/> must be paired with <see cref="Exit"/>
    /// in a <c>finally</c>, with the same client and key.
    /// </summary>
    public ValueTask<VerificationSlot> EnterAsync(IpKey client, CancellationToken cancellation, ulong key = 0)
    {
        if (cancellation.IsCancellationRequested)
        {
            return new(VerificationSlot.Cancelled);
        }

        var stripe = Stripe(client);
        Waiter waiter;
        lock (_lock)
        {
            if (_places[stripe] >= _maxPlacesPerClient)
            {
                _stats.CountQueueFull();
                return new(VerificationSlot.ClientFull);
            }

            // Every waiter that could run has been given a slot already, so a free slot and a request that may run is enough.
            if (_running < _slots && CanRun(stripe, key))
            {
                Start(stripe, key);
                _places[stripe]++;
                return new(VerificationSlot.Granted);
            }

            if (_queued >= _maxQueued)
            {
                _stats.CountQueueFull();
                return new(VerificationSlot.QueueFull);
            }

            _places[stripe]++;
            _queued++;
            _stats.CountQueued();
            waiter = _pool.TryPop(out var pooled) ? pooled : new Waiter(this);
            waiter.Enqueue(stripe, key, _time.GetTimestamp() + _maxWaitTicks, _tail);
            if (_tail is null)
            {
                _head = waiter;
            }
            else
            {
                _tail.Next = waiter;
            }

            _tail = waiter;
            if (!_timerPending)
            {
                ArmTimer(_maxWaitTicks);
            }
        }

        // After the lock: a registration on an already-cancelled token runs its callback at once, and that takes the lock.
        waiter.Watch(cancellation);
        return new(waiter, waiter.Version);
    }

    /// <summary>Gives a granted slot back and hands it to the first waiter that may run, if any.</summary>
    public void Exit(IpKey client, ulong key = 0)
    {
        var stripe = Stripe(client);
        Waiter? next;
        lock (_lock)
        {
            _running--;
            _places[stripe]--;
            _runningByClient[stripe]--;
            RemoveKey(key);
            next = TakeRunnable();
        }

        while (next is not null)
        {
            next.Complete(VerificationSlot.Granted);
            lock (_lock)
            {
                next = TakeRunnable();
            }
        }
    }

    private bool CanRun(int stripe, ulong key)
    {
        if (_runningByClient[stripe] >= _maxRunningPerClient)
        {
            return false;
        }

        if (key != 0)
        {
            for (var i = 0; i < _running; i++)
            {
                if (_runningKeys[i] == key)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void Start(int stripe, ulong key)
    {
        _runningKeys[_running] = key;
        _running++;
        _runningByClient[stripe]++;
    }

    // The slot's key goes with it: the last key moves into the hole, as the order of running keys means nothing.
    private void RemoveKey(ulong key)
    {
        for (var i = 0; i <= _running; i++)
        {
            if (_runningKeys[i] == key)
            {
                _runningKeys[i] = _runningKeys[_running];
                return;
            }
        }
    }

    // Moves a waiter from the line to a slot (the place it holds, and so its client's count, is unchanged).
    private Waiter? TakeRunnable()
    {
        if (_running >= _slots)
        {
            return null;
        }

        for (var waiter = _head; waiter is not null; waiter = waiter.Next)
        {
            if (CanRun(waiter.Stripe, waiter.Key))
            {
                Unlink(waiter);
                Start(waiter.Stripe, waiter.Key);
                return waiter;
            }
        }

        return null;
    }

    private void Leave(Waiter waiter, VerificationSlot outcome)
    {
        lock (_lock)
        {
            if (!waiter.Queued)
            {
                return;
            }

            Unlink(waiter);
            _places[waiter.Stripe]--;
        }

        waiter.Complete(outcome);
    }

    private void Expire()
    {
        while (true)
        {
            Waiter? due;
            lock (_lock)
            {
                var now = _time.GetTimestamp();
                if (_head is not { } head)
                {
                    _timerPending = false;
                    return;
                }

                if (head.Deadline > now)
                {
                    ArmTimer(head.Deadline - now);
                    return;
                }

                Unlink(head);
                _places[head.Stripe]--;
                _stats.CountQueueTimeout();
                due = head;
            }

            due.Complete(VerificationSlot.TimedOut);
        }
    }

    private void Unlink(Waiter waiter)
    {
        if (waiter.Previous is { } previous)
        {
            previous.Next = waiter.Next;
        }
        else
        {
            _head = waiter.Next;
        }

        if (waiter.Next is { } next)
        {
            next.Previous = waiter.Previous;
        }
        else
        {
            _tail = waiter.Previous;
        }

        waiter.Next = null;
        waiter.Previous = null;
        waiter.Queued = false;
        _queued--;
    }

    private void ArmTimer(long ticks)
    {
        var due = TimeSpan.FromSeconds((double)ticks / _time.TimestampFrequency);
        _timer ??= _time.CreateTimer(static state => ((AccessListVerificationQueue)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timerPending = true;
        _timer.Change(due < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : due, Timeout.InfiniteTimeSpan);
    }

    private void Return(Waiter waiter)
    {
        lock (_lock)
        {
            if (_pool.Count < MaxPooledWaiters)
            {
                _pool.Push(waiter);
            }
        }
    }

    private int Stripe(IpKey client)
    {
        var value = client.MaskTo(32, 64).Value;
        var hash = ((ulong)value ^ ((ulong)(value >> 64) * 0x9E37_79B9_7F4A_7C15UL) ^ _stripeSeed) * 0xFF51_AFD7_ED55_8CCDUL;
        return (int)((hash ^ (hash >> 32)) & (ClientStripes - 1));
    }

    private sealed class Waiter(AccessListVerificationQueue owner) : IValueTaskSource<VerificationSlot>
    {
        private ManualResetValueTaskSourceCore<VerificationSlot> _core = new() { RunContinuationsAsynchronously = true };
        private CancellationTokenRegistration _registration;

        public Waiter? Next { get; set; }

        public Waiter? Previous { get; set; }

        /// <summary>In the line: guarded by the owner's lock, so exactly one of grant, cancel and expiry takes a waiter out.</summary>
        public bool Queued { get; set; }

        public int Stripe { get; private set; }

        public ulong Key { get; private set; }

        public long Deadline { get; private set; }

        public short Version => _core.Version;

        public void Enqueue(int stripe, ulong key, long deadline, Waiter? previous)
        {
            Stripe = stripe;
            Key = key;
            Deadline = deadline;
            Previous = previous;
            Next = null;
            Queued = true;
        }

        public void Watch(CancellationToken cancellation)
        {
            if (cancellation.CanBeCanceled)
            {
                _registration = cancellation.UnsafeRegister(static (state, _) => ((Waiter)state!).Cancel(), this);
            }
        }

        public void Complete(VerificationSlot outcome) => _core.SetResult(outcome);

        private void Cancel() => owner.Leave(this, VerificationSlot.Cancelled);

        public VerificationSlot GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                _core.Reset();
                _registration.Dispose();
                _registration = default;
                owner.Return(this);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}
