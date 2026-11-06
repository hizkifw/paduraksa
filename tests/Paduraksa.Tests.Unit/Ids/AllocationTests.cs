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
        Span<char> chars = stackalloc char[Base62.Width128];
        Span<byte> bytes = stackalloc byte[Base62.Width128];
        Guid guid = UuidV4Generator.Next();

        long allocated = 0;
        for (int round = 0; round < 2; round++) // round 0 warms up (JIT, static ctors)
        {
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

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    private static void AssertNoAllocations<T>(T id)
        where T : struct, ITypedId<T>
    {
        Span<char> chars = stackalloc char[64];
        Span<byte> bytes = stackalloc byte[64];
        ReadOnlySpan<char> text = id.ToString();
        ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(id.ToString()!);

        long allocated = 0;
        for (int round = 0; round < 2; round++) // round 0 warms up (JIT, static ctors)
        {
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

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }
}
