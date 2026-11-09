using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Proxy.Logging;

/// <summary>The generated request ID's shape and the values it may adopt from a trusted proxy (PLAN §7.4).</summary>
public sealed class RequestIdsTests
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz-";

    [Fact]
    public void AGeneratedIdIsACounterInBase32BehindAProcessPrefix()
    {
        string first = RequestIds.Format(RequestIds.NextCounter());
        string second = RequestIds.Format(RequestIds.NextCounter());

        Assert.Equal(RequestIds.GeneratedLength, first.Length);
        Assert.Equal(RequestIds.GeneratedLength, second.Length);
        Assert.Equal(first[..8], second[..8]); // one prefix per process
        Assert.Equal('-', first[8]);
        Assert.NotEqual(first, second);
        Assert.All(first, c => Assert.Contains(c, Alphabet));
    }

    [Fact]
    public void CounterValuesAreDistinctUnderConcurrency()
    {
        long[] counters = new long[1_000];
        Parallel.For(0, counters.Length, i => counters[i] = RequestIds.NextCounter());

        Assert.Equal(counters.Length, counters.Distinct().Count());
        Assert.All(counters, static value => Assert.True(value > 0));
    }

    [Fact]
    public void TheFormatNeedsItsExactBuffer()
    {
        Span<char> buffer = stackalloc char[RequestIds.GeneratedLength];
        Assert.Equal(RequestIds.GeneratedLength, RequestIds.Format(42, buffer));
        Assert.Throws<ArgumentException>(() => RequestIds.Format(42, new char[RequestIds.GeneratedLength - 1]));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0k3m9x2p-00000000000ab")]
    [InlineData("docker-1234_deadbeef.cafe:42")]
    [InlineData("~!@#$%^&*()+={}[]|\\;:'\",.<>/?")]
    public void VisibleAsciiIsAccepted(string value)
    {
        Assert.True(RequestIds.IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\r\nbreak")]
    [InlineData("tab\there")]
    [InlineData("café")]
    [InlineData("😀")]
    public void SpacesControlCharactersAndNonAsciiAreRefused(string value)
    {
        Assert.False(RequestIds.IsValid(value));
    }

    [Fact]
    public void AnOverlongValueIsRefused()
    {
        Assert.True(RequestIds.IsValid(new string('a', RequestIds.MaxLength)));
        Assert.False(RequestIds.IsValid(new string('a', RequestIds.MaxLength + 1)));
    }
}
