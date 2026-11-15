using System.Reflection;
using System.Reflection.Emit;

using Paduraksa.Core.Hosting;

namespace Paduraksa.Tests.Unit.Hosting;

public sealed class BuildInfoTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void TheCommitTheSdkAppendsIsSplitOffTheVersion()
    {
        var info = BuildInfo.Read(Assembly("0.1.0+" + Sha));

        Assert.Equal("0.1.0", info.Version);
        Assert.Equal(Sha, info.Commit);
        Assert.Null(info.BuiltAt);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.0+")]
    public void ABuildWithoutACommitHasNone(string informational)
    {
        var info = BuildInfo.Read(Assembly(informational));

        Assert.Equal(informational.TrimEnd('+'), info.Version);
        Assert.Null(info.Commit);
    }

    [Fact]
    public void AReproducibleBuildDateKeepsItsTime()
    {
        var info = BuildInfo.Read(Assembly("0.1.0", buildDate: "2026-11-14T08:30:15Z"));

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 8, 30, 15, TimeSpan.Zero), info.BuiltAt);
        Assert.True(info.BuiltAtHasTime);
    }

    [Fact]
    public void ALocalBuildDateIsTheDayOnly()
    {
        var info = BuildInfo.Read(Assembly("0.1.0", buildDate: "2026-11-14"));

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), info.BuiltAt);
        Assert.False(info.BuiltAtHasTime);
    }

    [Fact]
    public void ADateThatIsNotOneIsLeftOut() =>
        Assert.Null(BuildInfo.Read(Assembly("0.1.0", buildDate: "yesterday")).BuiltAt);

    private static AssemblyBuilder Assembly(string informational, string? buildDate = null)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BuildInfoFixture"), AssemblyBuilderAccess.RunAndCollect);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]));
        if (buildDate is not null)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, [BuildInfo.BuildDateKey, buildDate]));
        }

        return assembly;
    }
}
