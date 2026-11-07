using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The non-web host that owns every <see cref="IHostedService"/> that must run once per process
/// (startup recompile, later the ACME/renewal scheduler, metric sampler, rate-limit sweeper, notifications).
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

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Core);

        // The vault first: the config store loads certificates through it, and a vault that cannot start must stop the process.
        builder.Services.AddHostedService<VaultStartup>();
        builder.Services.AddHostedService<ConfigStartup>();
        // Then what the vault holds that the database lost, before the web apps take their first request.
        builder.Services.AddHostedService<CertificateRecoveryStartup>();

        HostSetup.ApplyCallerServices(builder, context, HostRole.Core);
        return builder.Build();
    }
}
