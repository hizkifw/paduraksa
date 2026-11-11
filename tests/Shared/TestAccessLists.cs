// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration like TestIds.
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Tests.Shared;

/// <summary>Address-only access lists for tests that only need "these addresses get through": the list, and the policy that
/// names it.</summary>
internal static class TestAccessLists
{
    public static AccessListSpec Allow(string label, params string[] addresses) => new()
    {
        Id = TestIds.AccessList(label),
        Name = label,
        Realm = label,
        Allow = addresses,
    };

    public static AccessListSpec AllowAndDrop(string label, params string[] addresses) => Allow(label, addresses) with { DropDenied = true };

    public static AccessListPolicySpec Policy(this AccessListSpec list) => new(list.Id);
}
