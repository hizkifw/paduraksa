using Paduraksa.Application.Logging;
using Paduraksa.Contracts.Logging;
using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Unit.Logging;

public sealed class LogTailFilterTests
{
    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("  ", null, null, true)]
    [InlineData("404", null, 404, true)]
    [InlineData("5xx", 5, null, true)]
    [InlineData("4XX", 4, null, true)]
    [InlineData("2", 2, null, true)]
    [InlineData("6xx", null, null, false)]
    [InlineData("0", null, null, false)]
    [InlineData("99", null, null, false)]
    [InlineData("600", null, null, false)]
    [InlineData("-404", null, null, false)]
    [InlineData("4x", null, null, false)]
    [InlineData("teapot", null, null, false)]
    public void TheStatusFieldTakesAStatusOrAClass(string? text, int? expectedClass, int? expectedStatus, bool valid)
    {
        Assert.Equal(valid, LogTailFilter.TryParseStatus(text, out var statusClass, out var status));
        Assert.Equal(expectedClass, statusClass);
        Assert.Equal(expectedStatus, status);
    }

    [Theory]
    [InlineData(nameof(LogTailFilter.Host))]
    [InlineData(nameof(LogTailFilter.ClientAddress))]
    [InlineData(nameof(LogTailFilter.Method))]
    [InlineData(nameof(LogTailFilter.PathContains))]
    public void AServiceTakesNoFilterTextOverTheCapWhoeverAsks(string field)
    {
        LogTailService service = new(new AccessLogTail(8, AccessLogFormatOptions.Default));
        var text = new string('x', LogTailFilter.MaxTextLength + 1);
        var filter = field switch
        {
            nameof(LogTailFilter.Host) => new LogTailFilter(Host: text),
            nameof(LogTailFilter.ClientAddress) => new LogTailFilter(ClientAddress: text),
            nameof(LogTailFilter.Method) => new LogTailFilter(Method: text),
            _ => new LogTailFilter(PathContains: text),
        };

        Assert.Throws<ArgumentException>(() => service.Open(filter, 0));
        Assert.Throws<ArgumentException>(() => service.Recent(filter, 10));

        using var feed = service.Open(filter with { Host = null, ClientAddress = null, Method = null, PathContains = new string('x', LogTailFilter.MaxTextLength) }, 0);
        Assert.NotNull(feed);
    }
}
