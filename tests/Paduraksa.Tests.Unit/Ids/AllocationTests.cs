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
        long allocated = AllocatedOnDedicatedThread(static () =>
        {
            Span<char> chars = stackalloc char[Base62.Width128];
            Span<byte> bytes = stackalloc byte[Base62.Width128];
            Guid guid = UuidV4Generator.Next();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
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
        long allocated = AllocatedOnDedicatedThread(() =>
        {
            Span<char> chars = stackalloc char[64];
            Span<byte> bytes = stackalloc byte[64];
            ReadOnlySpan<char> text = id.ToString();
            ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(id.ToString()!);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                Assert.True(id.TryFormat(chars, out _, default, null));
                Assert.True(id.TryFormat(bytes, out _, default, null));
                Assert.True(T.TryParse(text, out T a) && a.Equals(id));
                Assert.True(T.TryParse(utf8, out T b) && b.Equals(id));
                _ = T.Parse(text);
                _ = id.CompareTo(a);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// Measures allocations on a thread of the test's own. <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts
    /// only its thread, but the thread-pool thread a test runs on is shared with the test framework (and with whatever it
    /// did there before); on a busy full run that showed up as a few kilobytes in a zero-allocation assertion. The
    /// measurement runs twice: the first time warms JIT, statics and the thread's own runtime state.
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
                    _ = measure();
                    allocated = measure();
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
