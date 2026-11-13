using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Data;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.AccessLists;

public sealed class AccessListVerificationQueueTests
{
    private static readonly IpKey Sprayer = IpKey.Parse("198.51.100.66");
    private static readonly IpKey Member = IpKey.Parse("203.0.113.9");
    private static readonly TimeSpan Patient = TimeSpan.FromMinutes(1);

    private static AccessListVerificationQueue Queue(
        out AccessListStats stats, int slots = 2, int maxQueued = 16, int perClient = 4, int running = 100, TimeSpan? maxWait = null, TimeProvider? time = null)
    {
        stats = new AccessListStats();
        return new AccessListVerificationQueue(time ?? TimeProvider.System, stats, slots, maxQueued, perClient, running, maxWait ?? Patient);
    }

    private static IpKey Client(int n) => IpKey.Parse($"192.0.2.{n}");

    private static async Task<VerificationSlot> EnterAsync(AccessListVerificationQueue queue, IpKey client, CancellationToken? cancellation = null) =>
        await queue.EnterAsync(client, cancellation ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task OnlyAsManyChecksRunAtOnceAsThereAreSlotsAndTheRestWait()
    {
        var queue = Queue(out var stats, slots: 2);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(2)));

        var third = queue.EnterAsync(Client(3), TestContext.Current.CancellationToken).AsTask();

        Assert.False(third.IsCompleted);
        Assert.Equal((2, 1), (queue.Running, queue.Queued));
        Assert.Equal(1, stats.Queued);

        queue.Exit(Client(1));

        Assert.Equal(VerificationSlot.Granted, await third);
        Assert.Equal((2, 0), (queue.Running, queue.Queued));
    }

    [Fact]
    public async Task TheLineIsFirstInFirstOut()
    {
        var queue = Queue(out _, slots: 1);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        var line = Enumerable.Range(2, 6).Select(n => (n, task: queue.EnterAsync(Client(n), TestContext.Current.CancellationToken).AsTask())).ToArray();

        var served = new List<int>();
        queue.Exit(Client(1));
        foreach (var (n, task) in line)
        {
            Assert.Equal(VerificationSlot.Granted, await task);
            served.Add(n);
            queue.Exit(Client(n));
        }

        Assert.Equal([2, 3, 4, 5, 6, 7], served);
    }

    [Fact]
    public async Task OneClientHoldsOnlyItsShareOfPlacesQueuedAndRunningTogether()
    {
        var queue = Queue(out var stats, slots: 1, perClient: 2);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Sprayer));
        var queued = queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken).AsTask();
        Assert.False(queued.IsCompleted);

        // Two places taken, one running and one in the line: no third, however empty the line is.
        Assert.Equal(VerificationSlot.ClientFull, await EnterAsync(queue, Sprayer));
        Assert.Equal((1, 1), (stats.QueueFull, stats.Busy));

        // Somebody else is untouched, and queues behind.
        var other = queue.EnterAsync(Member, TestContext.Current.CancellationToken).AsTask();
        Assert.False(other.IsCompleted);

        queue.Exit(Sprayer);
        Assert.Equal(VerificationSlot.Granted, await queued);
        queue.Exit(Sprayer);
        Assert.Equal(VerificationSlot.Granted, await other);
        queue.Exit(Member);
        Assert.Equal((0, 0), (queue.Running, queue.Queued));

        // Both places were given back.
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Sprayer));
    }

    [Fact]
    public async Task OneClientRunsOnlyAFewChecksAtOnceAndTheRestWaitWhileOthersOvertakeThem()
    {
        var queue = Queue(out _, slots: 4, perClient: 8, running: 2);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Sprayer));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Sprayer));

        // Two slots are free, and the third request of the same client still waits.
        var third = queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken).AsTask();
        Assert.False(third.IsCompleted);
        Assert.Equal((2, 1), (queue.Running, queue.Queued));

        // Somebody else, arriving later, is not made to wait behind it.
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Member));

        queue.Exit(Sprayer);
        Assert.Equal(VerificationSlot.Granted, await third);
        Assert.Equal((3, 0), (queue.Running, queue.Queued));
    }

    [Fact]
    public async Task AFreeSlotGoesToTheFirstWaiterThatMayRunNotTheFirstInTheLine()
    {
        var queue = Queue(out _, slots: 3, perClient: 8, running: 1);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Sprayer));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(2)));
        var again = queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken).AsTask();
        var other = queue.EnterAsync(Member, TestContext.Current.CancellationToken).AsTask();

        queue.Exit(Client(1));

        Assert.Equal(VerificationSlot.Granted, await other);
        Assert.False(again.IsCompleted);
        queue.Exit(Sprayer);
        Assert.Equal(VerificationSlot.Granted, await again);
    }

    [Fact]
    public async Task ACheckOfCredentialsThatAreBeingCheckedWaitsForTheFirstAndKeysOfNothingNeverWait()
    {
        var queue = Queue(out _, slots: 4, perClient: 8, running: 4);
        const ulong Same = 77;
        Assert.Equal(VerificationSlot.Granted, await queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken, Same));

        var twin = queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken, Same).AsTask();
        Assert.False(twin.IsCompleted);

        // A different password, or one with no key (a name nobody has), runs beside it.
        Assert.Equal(VerificationSlot.Granted, await queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken, 78));
        Assert.Equal(VerificationSlot.Granted, await queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken));
        Assert.Equal(VerificationSlot.Granted, await queue.EnterAsync(Sprayer, TestContext.Current.CancellationToken));

        queue.Exit(Sprayer, 78);
        Assert.False(twin.IsCompleted);
        queue.Exit(Sprayer, Same);
        Assert.Equal(VerificationSlot.Granted, await twin);
        queue.Exit(Sprayer, Same);
        queue.Exit(Sprayer);
        queue.Exit(Sprayer);
        Assert.Equal((0, 0), (queue.Running, queue.Queued));
    }

    [Fact]
    public async Task AnIpv6ClientSharesItsPlacesWithItsWholeSlash64()
    {
        var queue = Queue(out _, slots: 8, perClient: 2);

        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, IpKey.Parse("2001:db8:1:2::1")));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, IpKey.Parse("2001:db8:1:2::ffff")));
        Assert.Equal(VerificationSlot.ClientFull, await EnterAsync(queue, IpKey.Parse("2001:db8:1:2:1::1")));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, IpKey.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public async Task AFullLineRefusesTheNextRequestAtOnceAndKeepsWhatItHas()
    {
        var queue = Queue(out var stats, slots: 1, maxQueued: 2, perClient: 8);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        var first = queue.EnterAsync(Client(2), TestContext.Current.CancellationToken).AsTask();
        var second = queue.EnterAsync(Client(3), TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(VerificationSlot.QueueFull, await EnterAsync(queue, Client(4)));
        Assert.Equal((2, 1, 1), (stats.Queued, stats.QueueFull, stats.Busy));

        queue.Exit(Client(1));
        Assert.Equal(VerificationSlot.Granted, await first);
        queue.Exit(Client(2));
        Assert.Equal(VerificationSlot.Granted, await second);
        queue.Exit(Client(3));
        Assert.Equal((0, 0), (queue.Running, queue.Queued));
    }

    [Fact]
    public async Task AWaiterThatWaitsTheMaximumIsRefusedAndItsPlaceIsFree()
    {
        var queue = Queue(out var stats, slots: 1, perClient: 1, maxWait: TimeSpan.FromMilliseconds(150));
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));

        var timedOut = await queue.EnterAsync(Client(2), TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(VerificationSlot.TimedOut, timedOut);
        Assert.Equal((1, 0), (queue.Running, queue.Queued));
        Assert.Equal((1, 1, 1), (stats.Queued, stats.QueueTimeouts, stats.Busy));

        // Its client's one place came back: it can queue again, and the slot is still the first one's.
        var again = queue.EnterAsync(Client(2), TestContext.Current.CancellationToken).AsTask();
        Assert.False(again.IsCompleted);
        queue.Exit(Client(1));
        Assert.Equal(VerificationSlot.Granted, await again);
        queue.Exit(Client(2));
    }

    [Fact]
    public async Task OnlyTheWaitersPastTheirMaximumTimeOutTheOthersKeepTheirTurn()
    {
        ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var queue = Queue(out var stats, slots: 1, perClient: 8, maxWait: TimeSpan.FromMilliseconds(1500), time: time);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        var early = queue.EnterAsync(Client(2), TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(1000));
        var late = queue.EnterAsync(Client(3), TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(VerificationSlot.TimedOut, await early.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(late.IsCompleted);
        queue.Exit(Client(1));
        Assert.Equal(VerificationSlot.Granted, await late.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(1, stats.QueueTimeouts);
        queue.Exit(Client(3));
    }

    [Fact]
    public async Task ACancelledWaiterLeavesTheLineAtOnceAndFreesItsPlace()
    {
        var queue = Queue(out var stats, slots: 1, perClient: 1);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        using CancellationTokenSource gone = new();
        var abandoned = queue.EnterAsync(Client(2), gone.Token).AsTask();
        var behind = queue.EnterAsync(Client(3), TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(2, queue.Queued);

        await gone.CancelAsync();

        Assert.Equal(VerificationSlot.Cancelled, await abandoned);
        Assert.Equal(1, queue.Queued);
        Assert.Equal(0, stats.Busy);

        // The slot goes to the one behind, not to the cancelled request, and the cancelled client can queue again.
        var again = queue.EnterAsync(Client(2), TestContext.Current.CancellationToken).AsTask();
        Assert.False(again.IsCompleted);
        queue.Exit(Client(1));
        Assert.Equal(VerificationSlot.Granted, await behind);
        queue.Exit(Client(3));
        Assert.Equal(VerificationSlot.Granted, await again);
        queue.Exit(Client(2));
        Assert.Equal((0, 0), (queue.Running, queue.Queued));
    }

    [Fact]
    public async Task ARequestAlreadyCancelledTakesNoPlace()
    {
        var queue = Queue(out var stats, slots: 1);

        Assert.Equal(VerificationSlot.Cancelled, await EnterAsync(queue, Client(1), new CancellationToken(canceled: true)));

        Assert.Equal((0, 0, 0), (queue.Running, queue.Queued, stats.Queued));
    }

    [Fact]
    public async Task ACancellationAfterTheGrantChangesNothing()
    {
        var queue = Queue(out _, slots: 1);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Client(1)));
        using CancellationTokenSource gone = new();
        var waiting = queue.EnterAsync(Client(2), gone.Token).AsTask();

        queue.Exit(Client(1));
        await gone.CancelAsync();

        Assert.Equal(VerificationSlot.Granted, await waiting);
        Assert.Equal((1, 0), (queue.Running, queue.Queued));
        queue.Exit(Client(2));
        Assert.Equal(0, queue.Running);
    }

    [Fact]
    public async Task ManyConcurrentRequestsWithCancellationsAndTimeoutsNeverExceedTheSlotsAndLeaveNothingBehind()
    {
        const int Slots = 3;
        var queue = Queue(out var stats, slots: Slots, maxQueued: 40, perClient: 2, maxWait: TimeSpan.FromMilliseconds(80));
        var running = 0;
        var peak = 0;
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(async () =>
        {
            using CancellationTokenSource abandon = new();
            if (i % 7 == 0)
            {
                abandon.CancelAfter(TimeSpan.FromMilliseconds(i % 5));
            }

            var client = Client(1 + (i % 12));
            if (await queue.EnterAsync(client, abandon.Token) != VerificationSlot.Granted)
            {
                return;
            }

            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Interlocked.Increment(ref granted);
            // Work that holds a slot a moment: the stress test needs the slots occupied.
            await Task.Delay(1);
            Interlocked.Decrement(ref running);
            queue.Exit(client);
        }, TestContext.Current.CancellationToken)));

        Assert.True(peak <= Slots, $"{peak} checks ran at once");
        Assert.True(granted > 0);
        Assert.Equal((0, 0), (queue.Running, queue.Queued));
        Assert.Equal(stats.QueueFull + stats.QueueTimeouts, stats.Busy);
    }

    [Fact]
    public async Task ASlotThatIsFreeIsTakenWithoutAllocatingAnything()
    {
        var queue = Queue(out _, slots: 1);
        var client = Client(1);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, client));
        queue.Exit(client);

        AllocAssert.Zero((queue, client), static s =>
        {
            var task = s.queue.EnterAsync(s.client, default);
            if (task.IsCompletedSuccessfully && task.Result == VerificationSlot.Granted)
            {
                s.queue.Exit(s.client);
            }
        }, "taking and giving back a free slot");
    }

    [Fact]
    public async Task ARefusalAtOnceAllocatesNothingEither()
    {
        var queue = Queue(out _, slots: 1, perClient: 1);
        Assert.Equal(VerificationSlot.Granted, await EnterAsync(queue, Sprayer));

        AllocAssert.Zero((queue, Sprayer), static s =>
        {
            var task = s.queue.EnterAsync(s.Sprayer, default);
            if (task.IsCompletedSuccessfully)
            {
                _ = task.Result;
            }
        }, "a refusal at once");
        queue.Exit(Sprayer);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
        }
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
