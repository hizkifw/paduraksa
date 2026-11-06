using Microsoft.Extensions.Configuration;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>Cleanup after a failed build must never replace the original failure.</summary>
public sealed class FailedCreateTests
{
    [Fact]
    public void HostCreateSurfacesTheOriginalFailureWhenSharedDisposalThrows()
    {
        InvalidOperationException original = new("admin build failed");

        // The admin app is built last: core and public are built (and disposed again), then the shared services,
        // whose disposal throws an AggregateException that used to replace the original failure.
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => TestHost.Create(
            configureShared: static shared => shared.Add(new ThrowsOnDispose()),
            configureServices: (role, _) =>
            {
                if (role == HostRole.Admin)
                {
                    throw original;
                }
            }));

        Assert.Same(original, thrown);
    }

    [Fact]
    public void SharedServicesCreateSurfacesTheOriginalFailureWhenDisposalThrows()
    {
        InvalidOperationException original = new("configure failed");
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => SharedServices.Create(configuration, new PaduraksaOptions(), builder =>
        {
            builder.Add(new ThrowsOnDispose());
            throw original;
        }));

        Assert.Same(original, thrown);
    }

    private sealed class ThrowsOnDispose : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }
}
