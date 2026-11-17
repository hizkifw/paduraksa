using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class WordingTests
{
    [Theory]
    [InlineData(0, "0 events")]
    [InlineData(1, "1 event")]
    [InlineData(2, "2 events")]
    public void ACountTakesTheRightForm(int count, string expected) => Assert.Equal(expected, Wording.Plural(count, "event", "events"));

    [Theory]
    [InlineData(1, "1 event")]
    [InlineData(1204, "1,204 events")]
    public void AFormatGroupsTheDigitsOfTheCount(long count, string expected) =>
        Assert.Equal(expected, Wording.Plural(count, "event", "events", DisplayFormat.Invariant));

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b" }, "a and b")]
    [InlineData(new[] { "a", "b", "c" }, "a, b and c")]
    public void AListJoinsWithCommasAndAnAnd(string[] parts, string expected) => Assert.Equal(expected, Wording.List(parts));
}
