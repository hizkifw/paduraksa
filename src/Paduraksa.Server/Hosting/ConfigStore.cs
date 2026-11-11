using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Simulation;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Composes the config store into the shared services: the config database's context factory
/// (<c>&lt;data dir&gt;/paduraksa.db</c>), the compiler, the <see cref="LastKnownGoodFile"/>
/// (<c>&lt;data dir&gt;/last-good.json</c>) and the one <see cref="ConfigService"/> of the process, all registered as
/// shared instances, so the core host and both apps see the same ones. The core host's
/// <see cref="ConfigStartup"/> starts the service.
/// </summary>
/// <remarks>
/// The context factory and the service are built by a container of their own (EF Core's factory and the service's
/// constructor are meant to be resolved, not constructed); <see cref="SharedServices"/> owns that container and only it
/// disposes them. <b>Disposal</b> runs in reverse creation order, after every host has stopped: the container (the service
/// refuses new commands), then the publisher (it stops its writer and fails whatever is still queued, so nothing goes live
/// after this), then the last-known-good file, created before the publisher so it never trails the last revision that went
/// live.
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
        AcmeChallengeStore challenges,
        bool placeholderEnabled,
        AdminOptions admin,
        TimeProvider timeProvider,
        ProofOfWorkRuntime proofOfWork,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var compiler = builder.Add<IConfigCompiler>(new ConfigCompiler(listeners.CompilerOptions));

        // The public app attaches YARP's live health when it starts (PublicApp); until then every destination is unknown.
        var health = builder.Add(new DeferredUpstreamHealth());

        // The vault's certificates first; the placeholder (a source and a built-in inventory entry) only when it is enabled.
        var services = new ServiceCollection()
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
            .AddSingleton(vault.Totp)
            .AddPaduraksaIdentity(dataDirectory, admin.ToIdentityOptions())
            .AddSingleton<IUpstreamHealth>(health)
            .AddSingleton(new ListenerFacts(listeners.HttpProxyProtocol, listeners.HttpsProxyProtocol, listeners.Http3))
            .AddSingleton(challenges)
            .AddSingleton(proofOfWork)
            .AddPaduraksaConfigService()
            .AddPaduraksaCertificateOperations()
            .AddPaduraksaCertificateRecovery();
        if (placeholderEnabled)
        {
            services.AddSingleton<IBuiltInCertificates>(placeholder);
        }

        var container = builder.Own(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        var contexts = builder.AddOwnedElsewhere(container.GetRequiredService<IDbContextFactory<ConfigDbContext>>());
        var config = builder.AddOwnedElsewhere(container.GetRequiredService<ConfigService>());
        var certificates = builder.AddOwnedElsewhere(container.GetRequiredService<CertificateService>());
        var operations = builder.AddOwnedElsewhere(container.GetRequiredService<CertificateOperations>());
        builder.AddOwnedElsewhere(container.GetRequiredService<RequestSimulator>());
        builder.AddOwnedElsewhere(container.GetRequiredService<RevisionQueryService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<AuditQueryService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<CertificateRecovery>());
        builder.AddOwnedElsewhere(container.GetRequiredService<UserService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<SessionService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<TwoFactorService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<LoginService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<ApiTokenService>());
        var setup = builder.AddOwnedElsewhere(container.GetRequiredService<SetupService>());
        var permissions = builder.AddOwnedElsewhere(container.GetRequiredService<IPermissionEvaluator>());
        if (placeholderEnabled)
        {
            // The dashboard lists it with the inventory's certificates (source "built-in").
            builder.AddOwnedElsewhere<IBuiltInCertificates>(placeholder);
        }

        return new ConfigStoreServices(contexts, config, certificates, operations, setup, permissions);
    }
}

/// <summary>The config store's instances that later composition steps (ACME issuance, the admin certificate) build on.</summary>
/// <param name="Contexts">The config database's context factory.</param>
/// <param name="Config">The config write path.</param>
/// <param name="Certificates">The certificate inventory's service.</param>
/// <param name="Operations">The certificate use cases that touch the vault.</param>
/// <param name="Setup">First-run setup: whether an Owner exists, and the one-time setup token.</param>
/// <param name="Permissions">The one answer to "may this origin do that".</param>
internal sealed record ConfigStoreServices(
    IDbContextFactory<ConfigDbContext> Contexts,
    ConfigService Config,
    CertificateService Certificates,
    CertificateOperations Operations,
    SetupService Setup,
    IPermissionEvaluator Permissions);
