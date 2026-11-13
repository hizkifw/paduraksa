using Paduraksa.Admin.Api;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class VersionEndpointTests
{
    [Fact]
    public void TheCommitTheSdkAppendsToTheInformationalVersionIsSplitOff()
    {
        var info = VersionEndpoint.Read(typeof(VersionEndpoint).Assembly);

        Assert.Matches(@"^\d+\.\d+\.\d+", info.Version);
        Assert.DoesNotContain('+', info.Version);
        Assert.Equal("v1", info.Api);
        Assert.True(info.Commit is null || info.Commit.Length >= 7, "a commit, when there is one, is a SHA");
    }
}
