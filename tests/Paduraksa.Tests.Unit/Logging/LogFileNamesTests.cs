using Paduraksa.Core.Logging;

namespace Paduraksa.Tests.Unit.Logging;

public sealed class LogFileNamesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LiveFilesCarryTheirKind()
    {
        Assert.Equal(LogFileKind.Access, LogFileNames.KindOfLive("access.log"));
        Assert.Equal(LogFileKind.Error, LogFileNames.KindOfLive("error.log"));
        Assert.Null(LogFileNames.KindOfLive("access-20261109T120000Z.log"));
        Assert.Null(LogFileNames.KindOfLive("notes.txt"));
        Assert.Equal("access.log", LogFileNames.Live(LogFileKind.Access));
        Assert.Equal("error.log", LogFileNames.Live(LogFileKind.Error));
    }

    [Fact]
    public void ARotatedNameCarriesASortableUtcTimestamp()
    {
        string name = LogFileNames.Rotated(LogFileKind.Error, Noon);

        Assert.Equal("error-20261109T120000Z.log", name);
        // Within one second, the sequence keeps the names apart and in order.
        Assert.Equal("error-20261109T120000Z-2.log", LogFileNames.Rotated(LogFileKind.Error, Noon, 2));
        Assert.Equal("error-20261109T120000Z-10.log", LogFileNames.Rotated(LogFileKind.Error, Noon, 10));
        Assert.Equal("error-20261109T120000Z.log.gz", LogFileNames.Compressed(name));

        string earlier = LogFileNames.Rotated(LogFileKind.Error, Noon.AddSeconds(-1));
        string later = LogFileNames.Rotated(LogFileKind.Error, Noon.AddDays(1));
        Assert.True(string.CompareOrdinal(earlier, name) < 0);
        Assert.True(string.CompareOrdinal(name, later) < 0);
        // Two rotations in one second have the same timestamp; the suffix only keeps the names apart.
        Assert.NotEqual(name, LogFileNames.Rotated(LogFileKind.Error, Noon, 2));
        Assert.True(LogFileNames.TryParseRotated(LogFileNames.Rotated(LogFileKind.Error, Noon, 2), out _, out DateTimeOffset second, out _));
        Assert.Equal(Noon, second);
    }

    [Fact]
    public void ARotatedNameIsAlwaysUtcWhateverTheOffset()
    {
        DateTimeOffset local = new(2026, 11, 9, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("access-20261109T120000Z.log", LogFileNames.Rotated(LogFileKind.Access, local));
    }

    [Theory]
    [InlineData("error-20261109T120000Z.log", LogFileKind.Error, false)]
    [InlineData("access-20261109T120000Z.log.gz", LogFileKind.Access, true)]
    [InlineData("error-20261109T120000Z-2.log", LogFileKind.Error, false)]
    [InlineData("access-20261109T120000Z-17.log.gz", LogFileKind.Access, true)]
    public void ARotatedNameParsesBack(string name, LogFileKind kind, bool compressed)
    {
        Assert.True(LogFileNames.TryParseRotated(name, out LogFileKind parsed, out DateTimeOffset at, out bool isCompressed));

        Assert.Equal(kind, parsed);
        Assert.Equal(compressed, isCompressed);
        Assert.Equal(Noon, at);
        Assert.True(LogFileNames.IsLogFile(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("access.log.gz")]
    [InlineData("error.log.gz")]
    [InlineData("error-20261109T120000Z.log.gz.gz")]
    [InlineData("error-2026-11-09.log")]
    [InlineData("error-20261109T120000Z-1.log")]
    [InlineData("error-20261109T120000Z-0.log")]
    [InlineData("error-20261109T120000Z-x.log")]
    [InlineData("other-20261109T120000Z.log")]
    [InlineData("error-20261109T120000Z.txt")]
    [InlineData("../error-20261109T120000Z.log")]
    [InlineData("/var/log/error-20261109T120000Z.log")]
    [InlineData("error-20261109T120000Z.log ")]
    public void AnythingElseIsNotALogFile(string? name)
    {
        Assert.False(LogFileNames.TryParseRotated(name, out _, out _, out _));
        Assert.False(LogFileNames.IsLogFile(name));
    }

    [Fact]
    public void ALiveNameIsALogFileButNotARotatedOne()
    {
        Assert.True(LogFileNames.IsLogFile("error.log"));
        Assert.True(LogFileNames.IsLogFile("access.log"));
        Assert.False(LogFileNames.TryParseRotated("error.log", out _, out _, out _));
    }

    [Fact]
    public void ANonPositiveSequenceIsARefusedArgument()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LogFileNames.Rotated(LogFileKind.Error, Noon, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogFileNames.Stem((LogFileKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogFileNames.Live((LogFileKind)99));
    }
}
