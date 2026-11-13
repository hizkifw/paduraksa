using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Api;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class ApiQueryTests
{
    private static readonly QueryParameter Range = QueryParameter.Choice("range", "How far back.", ["hour", "day"], "hour");

    private static DefaultHttpContext Request(string query, params object[] metadata)
    {
        var http = new DefaultHttpContext { Request = { QueryString = new QueryString(query) } };
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(metadata), "GET /something"));
        return http;
    }

    [Fact]
    public void ADeclaredParameterIsRead()
    {
        var http = Request("?range=day", new QueryParameterMetadata(Range));

        Assert.Equal("day", Range.Read(http).ToString());
    }

    [Fact]
    public void ReadingAParameterTheEndpointDidNotDeclareIsABugThatThrowsBeforeItShips()
    {
        var http = Request("?range=day", new QueryParameterMetadata(ApiQueries.Limit));

        var ex = Assert.Throws<InvalidOperationException>(() => Range.Read(http));
        Assert.Contains("'range'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GET /something", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoEndpointToCheckAgainstTheParameterIsJustRead()
    {
        var http = new DefaultHttpContext { Request = { QueryString = new QueryString("?range=hour") } };

        Assert.Equal("hour", Range.Read(http).ToString());
    }

    [Fact]
    public void TheParametersEveryEndpointSharesAreDeclaredByTheConventionsThatReadThem()
    {
        var builder = new RecordingBuilder();

        builder.WithPage();
        builder.MayFailToPublish();

        Assert.Equal(["limit", "cursor", "dryRun", "force", "message"], builder.Declared.Select(m => m.Parameter.Name));
    }

    private sealed class RecordingBuilder : Microsoft.AspNetCore.Builder.IEndpointConventionBuilder
    {
        private readonly List<Action<Microsoft.AspNetCore.Builder.EndpointBuilder>> _conventions = [];

        public IEnumerable<QueryParameterMetadata> Declared
        {
            get
            {
                var builder = new TestEndpointBuilder();
                foreach (var convention in _conventions)
                {
                    convention(builder);
                }

                return builder.Metadata.OfType<QueryParameterMetadata>();
            }
        }

        public void Add(Action<Microsoft.AspNetCore.Builder.EndpointBuilder> convention) => _conventions.Add(convention);

        public void Finally(Action<Microsoft.AspNetCore.Builder.EndpointBuilder> finalConvention) => _conventions.Add(finalConvention);
    }

    private sealed class TestEndpointBuilder : Microsoft.AspNetCore.Builder.EndpointBuilder
    {
        public override Endpoint Build() => new(null, new EndpointMetadataCollection(Metadata), "test");
    }
}
