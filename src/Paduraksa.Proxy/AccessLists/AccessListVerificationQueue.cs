using System.Security.Cryptography;
using System.Threading.Tasks.Sources;

using Paduraksa.Proxy.Net;

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
/// a way in for a flood: a cap on the line's length, a cap on what one client (a masked address) may have queued and running
/// together, so a single source can only ever fill its own few places, and a maximum wait, after which the request is refused.
/// </summary>
/// <remarks>
/// <para><b>Waiting costs no thread.</b> A waiter is a pooled <see cref="IValueTaskSource{TResult}"/>; the request's
/// continuation is queued to the thread pool when its turn comes (never run inline on the thread that frees the slot, which
/// would stack one PBKDF2 on another). A request that gets a slot at once, the common case, allocates nothing here.</para>
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
    private readonly int _maxPerClient;
    private readonly long _maxWaitTicks;
    private readonly int[] _perClient = new int[ClientStripes];
    private readonly ulong _stripeSeed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    private readonly Stack<Waiter> _pool = new();
    private ITimer? _timer;
    private bool _timerPending;
    private int _running;
    private int _queued;
    private Waiter? _head;
    private Waiter? _tail;

    public AccessListVerificationQueue(TimeProvider time, AccessListStats stats, int slots, int maxQueued, int maxPerClient, TimeSpan maxWait)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueued);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerClient, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWait, TimeSpan.Zero);
        _time = time;
        _stats = stats;
        _slots = slots;
        _maxQueued = maxQueued;
        _maxPerClient = maxPerClient;
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
    /// Waits for a slot for <paramref name="client"/>. Synchronous (and allocation-free) when a slot is free or the request
    /// can't enter the line; otherwise it completes when a slot is handed over, the wait runs out or
    /// <paramref name="cancellation"/> fires. <see cref="VerificationSlot.Granted"/> must be paired with <see cref="Exit"/>
    /// in a <c>finally</c>.
    /// </summary>
    public ValueTask<VerificationSlot> EnterAsync(IpKey client, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
        {
            return new(VerificationSlot.Cancelled);
        }

        var stripe = Stripe(client);
        Waiter waiter;
        lock (_lock)
        {
            if (_perClient[stripe] >= _maxPerClient)
            {
                _stats.CountQueueFull();
                return new(VerificationSlot.ClientFull);
            }

            if (_running < _slots && _head is null)
            {
                _running++;
                _perClient[stripe]++;
                return new(VerificationSlot.Granted);
            }

            if (_queued >= _maxQueued)
            {
                _stats.CountQueueFull();
                return new(VerificationSlot.QueueFull);
            }

            _perClient[stripe]++;
            _queued++;
            _stats.CountQueued();
            waiter = _pool.TryPop(out var pooled) ? pooled : new Waiter(this);
            waiter.Enqueue(stripe, _time.GetTimestamp() + _maxWaitTicks, _tail);
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

    /// <summary>Gives a granted slot back and hands it to the waiter at the head of the line, if any.</summary>
    public void Exit(IpKey client)
    {
        var stripe = Stripe(client);
        lock (_lock)
        {
            _running--;
            _perClient[stripe]--;
        }

        Grant();
    }

    private void Grant()
    {
        while (true)
        {
            Waiter? next;
            lock (_lock)
            {
                if (_running >= _slots || _head is not { } head)
                {
                    return;
                }

                // The place the waiter already holds moves from the line to a slot: its client's count is unchanged.
                Unlink(head);
                _running++;
                next = head;
            }

            next.Complete(VerificationSlot.Granted);
        }
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
            _perClient[waiter.Stripe]--;
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
                _perClient[head.Stripe]--;
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

        public long Deadline { get; private set; }

        public short Version => _core.Version;

        public void Enqueue(int stripe, long deadline, Waiter? previous)
        {
            Stripe = stripe;
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
