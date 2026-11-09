using System.Reflection;

using Yarp.ReverseProxy.LoadBalancing;

using Contract = Paduraksa.Contracts.Config;
using Proxy = Paduraksa.Proxy;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// The document has its own enums (Contracts can't see Proxy); the mapping onto the compiler's model goes by member
/// name, so the two must keep the same members in the same order.
/// </summary>
public sealed class ContractEnumTests
{
    [Fact]
    public void DocumentEnumsMatchTheProxyEnums()
    {
        AssertSameMembers<Contract.HostKind, Proxy.Snapshot.HostKind>();
        AssertSameMembers<Contract.RouteMatchKind, Proxy.Snapshot.RouteMatchKind>();
        AssertSameMembers<Contract.TrustedProxyMode, Proxy.Net.TrustedProxyMode>();
        AssertSameMembers<Contract.TlsMinimumVersion, Proxy.Tls.TlsMinimumVersion>();
        AssertSameMembers<Contract.TcpAlpn, Proxy.Tls.TcpAlpn>();
        AssertSameMembers<Contract.ServerHeaderMode, Proxy.Config.ServerHeaderMode>();
        AssertSameMembers<Contract.UnknownHostBehaviour, Proxy.Config.UnknownHostMode>();
        AssertSameMembers<Contract.RequestIdPropagation, Proxy.Logging.RequestIdPropagation>();
    }

    [Fact]
    public void LoadBalancingPoliciesAreYarpPolicyNames()
    {
        string[] yarp =
        [
            .. typeof(LoadBalancingPolicies).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => (string)p.GetValue(null)!),
        ];

        Assert.All(Enum.GetNames<Contract.LoadBalancingPolicy>(), name => Assert.Contains(name, yarp));
    }

    private static void AssertSameMembers<TContract, TProxy>()
        where TContract : struct, Enum
        where TProxy : struct, Enum =>
        Assert.Equal(Enum.GetNames<TProxy>(), Enum.GetNames<TContract>());
}
