using System.CommandLine;

using Paduraksa.Server.Cli;

namespace Paduraksa.Tests.Integration.Cli;

public sealed class PaduraksaCliTests
{
    [Theory]
    [InlineData]
    [InlineData("serve")]
    public void NoArgumentsAndServeBothRunTheServer(params string[] args)
    {
        ParseResult result = PaduraksaCli.BuildRootCommand().Parse(args);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Action);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("serve", "--unknown")]
    public void UnknownCommandsAndOptionsAreRejected(params string[] args)
    {
        Assert.NotEmpty(PaduraksaCli.BuildRootCommand().Parse(args).Errors);
    }
}
