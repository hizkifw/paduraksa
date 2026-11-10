using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

using Paduraksa.Admin.OpenApi;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>The generated OpenAPI document is cached, but a failed generation must not be cached for the life of the process.</summary>
public sealed class OpenApiDocumentCacheTests
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFailedGenerationIsTriedAgainOnTheNextRequest()
    {
        FlakyProvider provider = new(failures: 1);
        using var services = Services(provider);
        OpenApiDocumentCache cache = new(services);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Ct));

        var document = System.Text.Encoding.UTF8.GetString(await cache.GetAsync(Ct));
        Assert.Contains("\"openapi\"", document, StringComparison.Ordinal);
        Assert.Equal(2, provider.Calls);

        // The successful document is the one that is kept.
        _ = await cache.GetAsync(Ct);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ConcurrentFirstRequestsGenerateTheDocumentOnce()
    {
        FlakyProvider provider = new(failures: 0);
        using var services = Services(provider);
        OpenApiDocumentCache cache = new(services);

        var documents = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync(Ct)));

        Assert.Equal(1, provider.Calls);
        Assert.All(documents, document => Assert.Equal(documents[0], document));
    }

    private static ServiceProvider Services(IOpenApiDocumentProvider provider) =>
        new ServiceCollection()
            .AddKeyedSingleton(AdminOpenApi.DocumentName, provider)
            .BuildServiceProvider();

    private sealed class FlakyProvider(int failures) : IOpenApiDocumentProvider
    {
        public int Calls { get; private set; }

        public Task<OpenApiDocument> GetOpenApiDocumentAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= failures)
            {
                throw new InvalidOperationException("The provider failed (a test).");
            }

            return Task.FromResult(new OpenApiDocument
            {
                Info = new OpenApiInfo { Title = "test", Version = "v1" },
                Paths = new OpenApiPaths(),
            });
        }
    }
}
