using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Placeholders;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Composes the config store (PLAN §6.1, §6.4) into the shared services: the config database's context factory
/// (<c>&lt;data dir&gt;/paduraksa.db</c>), the compiler, the <see cref="LastKnownGoodFile"/>
/// (<c>&lt;data dir&gt;/last-good.json</c>) and the one <see cref="ConfigService"/> of the process, all registered as
/// shared instances, so the core host and both apps see the same ones. The core host's
/// <see cref="ConfigStartup"/> starts the service.
/// </summary>
/// <remarks>
/// <para>The context factory and the service are built by a small container of their own (EF Core's factory and the
/// service's constructor are meant to be resolved, not constructed), whose instances are then shared like any other.
/// <see cref="SharedServices"/> owns that container, and the container alone disposes them.</para>
/// <para><b>Disposal</b> runs in reverse creation order, after every host has stopped: the container (the service
/// refuses new commands), then the publisher the service publishes with (it stops its writer and fails whatever is
/// still queued, so nothing goes live after this), then the last-known-good file, created before the publisher (it
/// waits for its pending write). So the file never trails the last revision that went live.</para>
/// </remarks>
internal static class ConfigStore
{
    /// <summary><c>&lt;data dir&gt;/last-good.json</c>; created on its own, before the publisher (see the remarks).</summary>
    public static LastKnownGoodFile CreateLastKnownGood(string dataDirectory, ILoggerFactory loggerFactory) =>
        new(Path.Combine(dataDirectory, LastKnownGoodFile.FileName), loggerFactory);

    public static ConfigStoreServices AddTo(
        SharedServices.Builder builder,
        string dataDirectory,
        SnapshotPublisher publisher,
        LastKnownGoodFile lastKnownGood,
        PublicListenerSetup listeners,
        PlaceholderCertificates placeholder,
        VaultCapabilities vault,
        bool placeholderEnabled,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        IConfigCompiler compiler = builder.Add<IConfigCompiler>(new ConfigCompiler(listeners.CompilerOptions));

        // The vault's certificates first; the placeholder (a source and a built-in inventory entry) only when it is enabled.
        IServiceCollection services = new ServiceCollection()
            .AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(dataDirectory, ConfigDatabase.FileName))
            .AddSingleton(compiler)
            .AddSingleton(publisher)
            .AddSingleton(timeProvider)
            .AddSingleton(loggerFactory)
            .AddSingleton(lastKnownGood)
            .AddSingleton<IServerCertificateSource>(new CompositeServerCertificateSource(vault.ServerCertificates, placeholderEnabled ? placeholder : null, PlaceholderCertificates.Id))
            .AddSingleton(vault.Issuance)
            .AddSingleton(vault.Import)
            .AddSingleton(vault.Catalog)
            .AddPaduraksaConfigService()
            .AddPaduraksaCertificateOperations()
            .AddPaduraksaCertificateRecovery();
        if (placeholderEnabled)
        {
            services.AddSingleton<IBuiltInCertificates>(placeholder);
        }

        ServiceProvider container = builder.Own(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        IDbContextFactory<ConfigDbContext> contexts = builder.AddOwnedElsewhere(container.GetRequiredService<IDbContextFactory<ConfigDbContext>>());
        ConfigService config = builder.AddOwnedElsewhere(container.GetRequiredService<ConfigService>());
        CertificateService certificates = builder.AddOwnedElsewhere(container.GetRequiredService<CertificateService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<CertificateOperations>());
        builder.AddOwnedElsewhere(container.GetRequiredService<CertificateRecovery>());
        return new ConfigStoreServices(contexts, config, certificates);
    }
}

/// <summary>The config store's instances that later composition steps (ACME issuance) build on.</summary>
/// <param name="Contexts">The config database's context factory.</param>
/// <param name="Config">The config write path.</param>
/// <param name="Certificates">The certificate inventory's service.</param>
internal sealed record ConfigStoreServices(IDbContextFactory<ConfigDbContext> Contexts, ConfigService Config, CertificateService Certificates);
