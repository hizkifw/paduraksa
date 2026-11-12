using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class WordingTests
{
    [Theory]
    [InlineData(0, "0 events")]
    [InlineData(1, "1 event")]
    [InlineData(2, "2 events")]
    public void ACountTakesTheRightForm(int count, string expected) => Assert.Equal(expected, Wording.Plural(count, "event", "events"));
}
