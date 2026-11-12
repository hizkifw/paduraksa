using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Identity;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>What the role picker says each role can do. The Operator's line must not understate it (threat model G5).</summary>
public sealed class RoleDescriptionTests
{
    [Fact]
    public void TheOperatorIsDescribedAsATrustedRole_WithWhatConfigWriteMakesPossible()
    {
        var text = IdentityText.Describe(Role.Operator);

        foreach (var power in new[] { "route to any internal address", "forward-auth endpoints", "trusted proxies", "access-list passwords", "A trusted role" })
        {
            Assert.Contains(power, text, StringComparison.Ordinal);
        }

        Assert.Contains("routes to any address", IdentityText.Describe(Permission.ConfigWrite), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRoleHasADescription()
    {
        Assert.All(IdentityText.Roles, role => Assert.False(string.IsNullOrWhiteSpace(IdentityText.Describe(role)), role.ToString()));
    }
}
