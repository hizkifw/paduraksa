using Paduraksa.Application.Hosting;
using Paduraksa.Application.Issuance;
using Paduraksa.Proxy.Logging;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The non-web host that owns every <see cref="IHostedService"/> that must run once per process: the startup
/// recompile, the renewal scheduler and whatever else is process-wide rather than per web app.
/// <para>
/// Why a third host instead of hanging them off the admin app: those services serve <i>both</i> apps, so their
/// lifetime must bracket both. The core host starts before the web apps (the public app never accepts a
/// connection without a snapshot) and stops after both (the public drain can still rely on challenge
/// answers, metrics and the sampler). Hanging them off the admin app would tie them to the admin's stop,
/// and an admin-only restart or failure would silently take proxy-side background work with it.
/// </para>
/// <para>Web apps must not register process-wide hosted services: they'd run twice or stop too early.</para>
/// </summary>
internal static class CoreHost
{
    public static IHost Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Core);
        // Background work that needs the listeners (the renewal scheduler) waits on the same signal.
        builder.Services.AddSingleton<IProcessReadiness>(static services => services.GetRequiredService<HealthState>());

        // The vault first: the config store loads certificates through it, and a vault that cannot start must stop the process.
        builder.Services.AddHostedService<VaultStartup>();
        builder.Services.AddHostedService<ConfigStartup>();
        builder.Services.AddHostedService<IdentityStartup>();
        // Then what the vault holds that the database lost, before the web apps take their first request: it is an awaited
        // start step (bounded by Paduraksa:Certificates:RecoveryStartupTimeout), registers quietly and queues one publish,
        // so the first snapshot the listeners serve already includes the recovered certificates.
        builder.Services.AddHostedService<CertificateRecoveryStartup>();
        // The admin listener's own certificate: the startup run is awaited, so the admin port serves the
        // local CA's certificate from its first request, and a daily loop renews it. It registers without republishing the
        // live config, so it never waits for the public app's YARP.
        builder.Services.AddHostedService(static services => services.GetRequiredService<AdminCertificateService>());
        // The access log's one reader: it formats the ring's entries into the sink, and flushes on shutdown.
        builder.Services.AddHostedService<AccessLogDrainService>();
        if (context.Options.Acme.Enabled)
        {
            // Last of the ones that start things up: the vault and the config store are ready. Its first look waits
            // SchedulerStartDelay and then IProcessReadiness (every listener bound), so an HTTP-01 validation never runs
            // before the public listener can answer it. It stops first, before the config store it records into.
            builder.Services.AddHostedService(static services => services.GetRequiredService<AcmeRenewalScheduler>());
        }

        HostSetup.ApplyCallerServices(builder, context, HostRole.Core);
        return builder.Build();
    }
}
