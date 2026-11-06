using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Snapshot;

public sealed class RoutePathTests
{
    // Inputs are paths as Kestrel hands them over: percent-decoded (except %2F), dot segments resolved on '/'.
    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/admin/x", "/admin/x")]
    [InlineData("//admin/x", "/admin/x")]
    [InlineData("/admin//x///", "/admin/x/")]
    [InlineData("/admin;a/x", "/admin/x")]
    [InlineData("/admin;jsessionid=1", "/admin")]
    [InlineData("/admin\\x", "/admin/x")]
    [InlineData("/admin%2Fx", "/admin/x")]
    [InlineData("/admin%2fx", "/admin/x")]
    [InlineData("/admin%5Cx", "/admin/x")]
    [InlineData("/admin ", "/admin")]
    [InlineData("/admin.", "/admin")]
    [InlineData("/admin. . /x", "/admin/x")]
    [InlineData("/x\\..\\admin", "/admin")]
    [InlineData("/x/./admin", "/x/admin")]
    [InlineData("/../../admin", "/admin")]
    [InlineData("/...", "/")]
    [InlineData("/a%2", "/a%2")]
    [InlineData("/file.txt", "/file.txt")]
    [InlineData("/ADMIN/X", "/ADMIN/X")]
    public void CanonicalViewNormalisesTheSpellingsUpstreamsTreatAlike(string path, string expected)
    {
        char[] buffer = new char[path.Length + 1];
        Assert.Equal(expected, new string(buffer, 0, RoutePath.Canonicalize(path, buffer)));
        Assert.Equal(expected, RoutePath.Canonicalize(path));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/api", true)]
    [InlineData("/api/", true)]
    [InlineData("/file.txt/x", true)]
    [InlineData("", false)]
    [InlineData("api", false)]
    [InlineData("//api", false)]
    [InlineData("/a//b", false)]
    [InlineData("/a;b", false)]
    [InlineData("/a\\b", false)]
    [InlineData("/a%2Fb", false)]
    [InlineData("/a.", false)]
    [InlineData("/a /b", false)]
    [InlineData("/a/..", false)]
    public void IsCanonicalAgreesWithCanonicalize(string path, bool canonical)
    {
        Assert.Equal(canonical, RoutePath.IsCanonical(path));
        if (canonical)
        {
            Assert.Equal(path, RoutePath.Canonicalize(path));
        }
    }

    [Fact]
    public void CanonicalisingDoesNotAllocate()
    {
        string[] paths = ["//admin/x", "/admin;a/x", "/admin%2Fx", "/x\\..\\admin"];
        char[] buffer = new char[64];
        AllocAssert.Zero(
            (paths, buffer),
            static s =>
            {
                foreach (string path in s.paths)
                {
                    _ = RoutePath.IsCanonical(path);
                    _ = RoutePath.Canonicalize(path, s.buffer);
                }
            },
            "RoutePath");
    }
}
