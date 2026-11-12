using Paduraksa.Contracts.Logging;

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
}
