using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>Cleanup after a failed build must never replace the original failure, and is logged instead.</summary>
public sealed class FailedCreateTests
{
    [Fact]
    public void HostCreateSurfacesTheOriginalFailureWhenSharedDisposalThrows()
    {
        InvalidOperationException original = new("admin build failed");
        LogCapture logs = new();

        // The admin app is built last: core and public are built (and disposed again), then the shared services,
        // whose disposal throws an AggregateException that used to replace the original failure.
        var thrown = Assert.Throws<InvalidOperationException>(() => TestHost.Create(
            configureShared: static shared => shared.Add(new ThrowsOnDispose()),
            configureServices: (role, _) =>
            {
                if (role == HostRole.Admin)
                {
                    throw original;
                }
            },
            logs: logs));

        Assert.Same(original, thrown);
        AssertCleanupFailureLogged(logs, typeof(PaduraksaHost));
    }

    [Fact]
    public void SharedServicesCreateSurfacesTheOriginalFailureWhenDisposalThrows()
    {
        InvalidOperationException original = new("configure failed");
        LogCapture logs = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        var thrown = Assert.Throws<InvalidOperationException>(() => SharedServices.Create(
            configuration,
            new PaduraksaOptions(),
            builder =>
            {
                builder.Add(new ThrowsOnDispose());
                throw original;
            },
            logging => logging.AddProvider(logs)));

        Assert.Same(original, thrown);
        AssertCleanupFailureLogged(logs, typeof(SharedServices));
    }

    private static void AssertCleanupFailureLogged(LogCapture logs, Type category)
    {
        var entry = Assert.Single(logs.Entries, static e => e.Message.StartsWith("Cleanup after a failure failed too", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(category.FullName, entry.Category);
        var cleanupError = Assert.IsType<AggregateException>(entry.Exception);
        Assert.Equal("dispose failed", Assert.Single(cleanupError.InnerExceptions).Message);
    }

    private sealed class ThrowsOnDispose : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }
}
