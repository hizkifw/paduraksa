using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Config;

public sealed class AccessListUserToStringTests
{
    [Fact]
    public void ThePlainPasswordOfAWriteIsNeverPartOfItsText()
    {
        var user = new AccessListUserConfig("alice", PasswordCredentialId.New(), "correct horse battery staple");
        var list = new AccessListConfig(AccessListId.New(), "office", BasicAuth: new AccessListBasicAuth(Users: [user]));

        Assert.DoesNotContain("correct horse", user.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", $"{user} {list} {new UpsertAccessListCommand(list)}", StringComparison.Ordinal);
        Assert.Contains("alice", user.ToString(), StringComparison.Ordinal);
    }
}
