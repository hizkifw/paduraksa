using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.Primitives;

using Paduraksa.Admin.Auth;
using Paduraksa.Core.Hosting;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>The authenticated internal hop and the single-valued forwarded headers it protects (PLAN §3.1).</summary>
public sealed class AdminForwardedHeadersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("paduraksa-forwarded-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task ACommaListIsNotAChain()
    {
        await using TestContext context = await TestContext.CreateAsync(_root);

        await context.InvokeAsync(x => x.Request.Headers["X-Forwarded-For"] = "203.0.113.1, 198.51.100.7");

        Assert.True(context.NextCalled);
        Assert.Null(context.HttpContext.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task RepeatedHeaderLinesAreUnknown()
    {
        await using TestContext context = await TestContext.CreateAsync(_root);

        await context.InvokeAsync(x =>
        {
            x.Request.Headers["X-Forwarded-For"] = new StringValues(["203.0.113.1", "198.51.100.7"]);
        });

        Assert.Null(context.HttpContext.Connection.RemoteIpAddress);
    }

    [Fact]
    public async Task AnIpv4MappedAddressIsShownAsIpv4()
    {
        await using TestContext context = await TestContext.CreateAsync(_root);

        await context.InvokeAsync(x => x.Request.Headers["X-Forwarded-For"] = "::ffff:203.0.113.1");

        Assert.Equal(IPAddress.Parse("203.0.113.1"), context.HttpContext.Connection.RemoteIpAddress);
        Assert.Equal(AddressFamily.InterNetwork, context.HttpContext.Connection.RemoteIpAddress!.AddressFamily);
    }

    [Fact]
    public async Task AnInvalidProtoIsIgnored()
    {
        await using TestContext context = await TestContext.CreateAsync(_root);

        await context.InvokeAsync(x =>
        {
            x.Request.Headers["X-Forwarded-Proto"] = "ftp";
            x.Request.Headers["X-Forwarded-Host"] = "admin.example.com";
        });

        Assert.Equal("http", context.HttpContext.Request.Scheme);
        Assert.Equal("admin.example.com", context.HttpContext.Request.Host.Value);
    }

    [Fact]
    public async Task AMissingSecretRefusesTheHopAndTrustsNothing()
    {
        await using TestContext context = await TestContext.CreateAsync(_root);

        await context.InvokeAsync(x =>
        {
            x.Request.Headers.Remove(AdminInternalListener.HeaderName);
            x.Request.Headers["X-Forwarded-For"] = "203.0.113.1";
            x.Request.Headers["X-Forwarded-Host"] = "evil.test";
        });

        Assert.False(context.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.HttpContext.Response.StatusCode);
        Assert.Null(context.HttpContext.Connection.RemoteIpAddress);
        Assert.NotEqual("evil.test", context.HttpContext.Request.Host.Value);
        Assert.Contains("admin.internal_hop", context.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestrictedAllowlistRefusesAnUnknownClientAddress()
    {
        await using TestContext context = await TestContext.CreateAsync(_root, allowedSources: ["127.0.0.1/32"]);

        await context.InvokeAsync();

        Assert.False(context.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.HttpContext.Response.StatusCode);
        Assert.Contains("admin.source.denied", context.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidHopRewritesTheClientSchemeAndHost()
    {
        await using TestContext context = await TestContext.CreateAsync(_root);

        await context.InvokeAsync(x =>
        {
            x.Request.Headers["X-Forwarded-For"] = "203.0.113.1";
            x.Request.Headers["X-Forwarded-Proto"] = "https";
            x.Request.Headers["X-Forwarded-Host"] = "admin.example.com:8443";
        });

        Assert.True(context.NextCalled);
        Assert.Equal(IPAddress.Parse("203.0.113.1"), context.HttpContext.Connection.RemoteIpAddress);
        Assert.Equal("https", context.HttpContext.Request.Scheme);
        Assert.Equal("admin.example.com:8443", context.HttpContext.Request.Host.Value);
        Assert.True(context.HttpContext.IsInternalAdminRequest());
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly AdminInternalListener _listener;
        private readonly TestConnectionItemsFeature _items = new();

        private TestContext(AdminInternalListener listener, AdminSourcePolicy sources)
        {
            _listener = listener;
            HttpContext = new DefaultHttpContext();
            HttpContext.Request.Scheme = "http";
            HttpContext.Features.Set<IConnectionItemsFeature>(_items);
            _items.Items[AdminInternalConnectionMarker.Key] = true;
            HttpContext.Request.Headers[AdminInternalListener.HeaderName] = listener.Secret;
            HttpContext.Response.Body = new MemoryStream();
            Middleware = new AdminForwardedHeadersMiddleware(
                _ => { NextCalled = true; return Task.CompletedTask; },
                listener,
                sources);
        }

        public DefaultHttpContext HttpContext { get; }

        public AdminForwardedHeadersMiddleware Middleware { get; }

        public bool NextCalled { get; private set; }

        public string Body
        {
            get
            {
                HttpContext.Response.Body.Position = 0;
                using StreamReader reader = new(HttpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
                return reader.ReadToEnd();
            }
        }

        public static Task<TestContext> CreateAsync(string root, IReadOnlyList<string>? allowedSources = null)
        {
            AdminInternalListener listener = new(root, enabled: true);
            return Task.FromResult(new TestContext(listener, new AdminSourcePolicy(allowedSources ?? [])));
        }

        public Task InvokeAsync(Action<DefaultHttpContext>? arrange = null)
        {
            arrange?.Invoke(HttpContext);
            return Middleware.InvokeAsync(HttpContext);
        }

        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestConnectionItemsFeature : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    }
}
