using System.Reflection;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>The proxy strips the admin session cookie by name but cannot reference Admin: the two spellings must stay equal.</summary>
public sealed class AdminCookieNameTests
{
    [Fact]
    public void TheProxyStripsExactlyTheCookieTheAdminSets()
    {
        var admin = LoadAssembly(Layer.Admin).GetType("Paduraksa.Admin.Auth.AdminSessionCookie")!.GetField("Name")!.GetRawConstantValue();
        var proxy = LoadAssembly(Layer.Proxy).GetType("Paduraksa.Proxy.Yarp.SessionCookieTransform")!
            .GetField("CookieName", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();

        Assert.Equal("__Host-paduraksa-session", admin);
        Assert.Equal(admin, proxy);
    }
}
