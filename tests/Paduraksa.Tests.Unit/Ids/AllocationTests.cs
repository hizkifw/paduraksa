using System.Runtime.ExceptionServices;
using System.Text;

using Paduraksa.Core.Ids;

namespace Paduraksa.Tests.Unit.Ids;

public sealed class AllocationTests
{
    [Fact]
    public void FormatAndParseOverSpansDoNotAllocate()
    {
        AssertNoAllocations(HostId.New());
        AssertNoAllocations(ChallengeId.New());
        AssertNoAllocations(AuditEventId.New());
    }

    [Fact]
    public void Base62OverSpansDoesNotAllocate()
    {
        var allocated = AllocatedOnDedicatedThread(static () =>
        {
            Span<char> chars = stackalloc char[Base62.Width128];
            Span<byte> bytes = stackalloc byte[Base62.Width128];
            var guid = UuidV4Generator.Next();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                Base62.TryEncode(guid, chars);
                Base62.TryEncode(guid, bytes);
                Base62.TryDecodeGuid(chars, out _);
                Base62.TryDecodeGuid(bytes, out _);
                Base62.TryEncode((ulong)i, chars);
                Base62.TryDecodeUInt64(chars[..Base62.Width64], out _);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.Equal(0, allocated);
    }

    private static void AssertNoAllocations<T>(T id)
        where T : struct, ITypedId<T>
    {
        var allocated = AllocatedOnDedicatedThread(() =>
        {
            Span<char> chars = stackalloc char[64];
            Span<byte> bytes = stackalloc byte[64];
            ReadOnlySpan<char> text = id.ToString();
            ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(id.ToString()!);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                Assert.True(id.TryFormat(chars, out _, default, null));
                Assert.True(id.TryFormat(bytes, out _, default, null));
                Assert.True(T.TryParse(text, out var a) && a.Equals(id));
                Assert.True(T.TryParse(utf8, out var b) && b.Equals(id));
                _ = T.Parse(text);
                _ = id.CompareTo(a);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// Measures allocations on a thread of the test's own, after three warm-up rounds, and takes the <b>smallest</b> of three
    /// measured rounds. A full run once reported about 7.9 KB for code with no managed allocations. The exact source of that
    /// one-off was not proven; the likely candidates are tiered compilation/OSR and the test framework's own work on the
    /// thread-pool thread it handed the test. A dedicated thread keeps framework work out, and the warm-ups move tiering and
    /// static initialization ahead of the measured rounds. A one-off lands in one round and the smallest of three drops it;
    /// an allocation in the measured loop itself happens in every round, so the assertion stays exactly zero for it.
    /// </summary>
    private static long AllocatedOnDedicatedThread(Func<long> measure)
    {
        long allocated = 0;
        Exception? failure = null;
        Thread thread = new(
            () =>
            {
                try
                {
                    for (var round = 0; round < 3; round++)
                    {
                        _ = measure(); // warm-up: JIT, tiering/OSR, statics, the thread's runtime state
                    }

                    var smallest = long.MaxValue;
                    for (var round = 0; round < 3; round++)
                    {
                        smallest = Math.Min(smallest, measure());
                    }

                    allocated = smallest;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            maxStackSize: 1024 * 1024)
        {
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return allocated;
    }
}
