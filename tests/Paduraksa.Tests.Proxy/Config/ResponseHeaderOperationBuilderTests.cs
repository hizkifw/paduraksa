using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Http;

namespace Paduraksa.Tests.Proxy.Config;

public sealed class ResponseHeaderOperationBuilderTests
{
    [Fact]
    public void TheCompilerRefusesMoreHeadersThanTheGateCanHold()
    {
        List<CompileError> errors = [];
        ResponseHeaderOperationBuilder builder = new(CompileErrorSource.Defaults, errors);
        for (int i = 0; i < ResponseHeaders.MaxResponseHeaderOperations; i++)
        {
            builder.Add(Operation($"X-{i}"));
        }

        Assert.Empty(errors);
        Assert.Equal(ResponseHeaders.MaxResponseHeaderOperations, builder.Build().Length);

        builder.Add(Operation("X-over"));

        Assert.Equal(ResponseHeaders.MaxResponseHeaderOperations, builder.Build().Length);
        Assert.Equal(
            $"a host has at most {ResponseHeaders.MaxResponseHeaderOperations} response-header operations",
            Assert.Single(errors).Message);
    }

    private static ResponseHeaderOperation Operation(string name) =>
        new(name, "v", ResponseHeaderKind.Set, ResponseHeaderStage.AfterDecision, ResponseHeaderConditions.None);
}
