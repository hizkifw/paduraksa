using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

using Paduraksa.Acme;
using Paduraksa.Application.Hosting;
using Paduraksa.Application.Issuance;
using Paduraksa.Core.Acme;
using Paduraksa.Core.Options;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Composes ACME issuance into the shared services: <c>AddPaduraksaIssuance</c> over the real vault (account keys,
/// MAC keys, secrets, the certificate catalog and issuance), the config store and the one <see cref="IAcmeChallengeStore"/> the
/// public gate answers from, in a container of its own that <see cref="SharedServices"/> owns, like <see cref="ConfigStore"/>.
/// The account, DNS provider and issuance services and the renewal scheduler are shared instances.
/// </summary>
/// <remarks>
/// <b>The scheduler</b> is not started here but by the core host, after <see cref="VaultStartup"/> and
/// <see cref="ConfigStartup"/>; its first look waits <c>Paduraksa:Acme:SchedulerStartDelay</c> and then
/// <see cref="IProcessReadiness"/> (every listener bound), so an HTTP-01 validation never runs before the public listener
/// can answer it.
///
/// <b>CA trust.</b> One transport serves every ACME client of the process: <see cref="AcmeHttp.CreateHandler()"/> (no
/// redirects), plus the roots of <c>Paduraksa:Acme:TrustedRootsFile</c> when set (a private CA, Pebble), which must be
/// self-signed CA certificates (anything else stops the start with the file named). The file is read once, here, and the
/// handler is owned here and disposed after the container that holds the clients.
///
/// <b>DNS providers</b> (<c>AddPaduraksaDnsProviders</c>: Cloudflare, deSEC and the authoritative-NS propagation check) run
/// over the vault's secret handler factory, so provider code never holds a token.
/// </remarks>
internal static class IssuanceStore
{
    /// <summary>The issuance options the bootstrap configuration gives; the DNS-01 wait and check are then server settings over them.</summary>
    public static AcmeIssuanceOptions IssuanceOptions(PaduraksaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var acme = options.Acme;
        return new()
        {
            SchedulerStartDelay = acme.SchedulerStartDelay,
            SchedulerPollInterval = acme.SchedulerPollInterval,
            DnsPropagationDelay = acme.DnsPropagationDelay,
            DnsPropagationCheck = acme.DnsPropagation.Enabled,
        };
    }

    public static void AddTo(
        SharedServices.Builder builder,
        PaduraksaOptions options,
        ConfigStoreServices configStore,
        VaultCapabilities vault,
        IAcmeChallengeStore challenges,
        IProcessReadiness readiness,
        LiveOptions<AcmeIssuanceOptions> issuance,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(readiness);

        var acme = options.Acme;
        AcmeClientOptions clientOptions = new() { AllowInsecureHttp = options.Development.AcmeInsecureHttp };
        var issuanceOptions = issuance.Current;

        // Created before the container, so disposed after it: the clients are done with it by then.
        var transport = builder.Own(CreateTransport(acme.TrustedRootsFile));

        var container = builder.Own(new ServiceCollection()
            .AddSingleton(loggerFactory)
            .AddSingleton(timeProvider)
            .AddSingleton(configStore.Contexts)
            .AddSingleton(configStore.Config)
            .AddSingleton(configStore.Certificates)
            .AddSingleton(configStore.Permissions)
            .AddSingleton(configStore.Events)
            .AddSingleton(vault.Issuance)
            .AddSingleton(vault.Catalog)
            .AddSingleton(vault.Signer)
            .AddSingleton(vault.MacKeys)
            .AddSingleton(vault.Secrets)
            .AddSingleton(vault.SecretHandlers)
            .AddSingleton(challenges)
            .AddSingleton(readiness)
            .AddSingleton(clientOptions)
            .AddSingleton(issuanceOptions)
            .AddSingleton(issuance)
            .AddSingleton<IAcmeClientProvider>(sp => new AcmeClientProvider(
                vault.Signer,
                vault.MacKeys,
                clientOptions,
                () => transport,
                timeProvider,
                sp.GetRequiredService<ILoggerFactory>()))
            .AddPaduraksaDnsProviders(acme.DnsPropagation)
            .AddPaduraksaIssuance()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        builder.AddOwnedElsewhere(container.GetRequiredService<AcmeAccountService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<DnsProviderService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<AcmeIssuanceService>());
        builder.AddOwnedElsewhere(container.GetRequiredService<AcmeRenewalScheduler>());
    }

    /// <summary>The transport of every ACME client: the roots of the PEM file, read once, are copied into it and the originals disposed.</summary>
    private static SocketsHttpHandler CreateTransport(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return AcmeHttp.CreateHandler();
        }

        X509Certificate2Collection roots = [];
        try
        {
            try
            {
                roots.ImportFromPem(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
            {
                throw Refused(file, $"can't be read as PEM certificates: {ex.Message}");
            }

            if (roots.Count == 0)
            {
                throw Refused(file, "holds no certificate.");
            }

            foreach (var root in roots)
            {
                if (!AcmeHttp.IsSelfSignedCa(root))
                {
                    throw Refused(file, $"holds {root.SubjectName.Name}, which is not a self-signed CA certificate; only roots may be listed (not an intermediate or a leaf).");
                }
            }

            // The handler keeps copies, so the originals are disposed below.
            return AcmeHttp.CreateHandler([.. roots.Cast<X509Certificate2>()]);
        }
        finally
        {
            foreach (var root in roots)
            {
                root.Dispose();
            }
        }
    }

    private static OptionsValidationException Refused(string file, string reason) =>
        new(PaduraksaOptions.SectionName, typeof(PaduraksaOptions), [$"{PaduraksaOptions.SectionName}:Acme:TrustedRootsFile '{file}' {reason}"]);
}
