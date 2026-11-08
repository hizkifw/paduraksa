using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Paduraksa.Admin.DataProtection;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>Everything the three host builders get from <see cref="PaduraksaHost"/>; built once per process.</summary>
internal sealed record HostContext(
    string ApplicationName,
    string EnvironmentName,
    string ContentRootPath,
    IConfiguration Configuration,
    PaduraksaOptions Options,
    SharedServices Shared,
    Action<HostRole, IServiceCollection>? ConfigureServices);

/// <summary>Setup common to the core host and both web apps.</summary>
internal static class HostSetup
{
    /// <summary>
    /// Wires a host builder into the process: the one shared configuration, the shared singletons, a
    /// passive lifetime (signals are handled once, by <see cref="PaduraksaHost.RunAsync"/>) and the shutdown timeout.
    /// </summary>
    public static void ApplyCommon(IHostApplicationBuilder builder, HostContext context, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ApplySharedConfiguration(builder.Configuration, context);
        ApplySharedServices(builder.Services, context, role);
    }

    /// <summary>The configuration half of <see cref="ApplyCommon"/>, for a generic <see cref="IHostBuilder"/>.</summary>
    public static void ApplySharedConfiguration(IConfigurationBuilder configuration, HostContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        // Every host reads the same, already-built configuration root (not disposed by the host).
        configuration.AddConfiguration(context.Configuration, shouldDisposeConfiguration: false);
    }

    /// <summary>The services half of <see cref="ApplyCommon"/>, for a generic <see cref="IHostBuilder"/>.</summary>
    public static void ApplySharedServices(IServiceCollection services, HostContext context, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        context.Shared.RegisterInto(services, role);

        // PLAN §8.1, §8.2: the admin app's Data Protection ring lives in the vault (the dashboard's antiforgery tokens and
        // circuit state must survive a restart). The public app only needs YARP's session-affinity key, so it gets an
        // in-memory ephemeral provider and never touches the vault; the core host resolves none.
        if (role == HostRole.Admin)
        {
            services.AddPaduraksaDataProtection(context.Shared.KeyRing);
        }
        else if (role == HostRole.Public)
        {
            EphemeralDataProtection.Add(services);
        }

        // ConsoleLifetime would hook SIGINT/SIGTERM in every host and stop each one independently.
        // PaduraksaHost owns signals and the stop order instead.
        services.RemoveAll<IHostLifetime>();
        services.AddSingleton<IHostLifetime>(new PassiveHostLifetime());

        // PaduraksaHost enforces one shutdown budget across all three hosts; the per-host timeout is disabled.
        services.Configure<HostOptions>(static host => host.ShutdownTimeout = Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// For the two web apps: pins the configuration keys the web host would otherwise use to add or replace
    /// listeners (<c>urls</c>, <c>http_ports</c>, <c>https_ports</c>, <c>preferHostingUrls</c>), so an unprefixed
    /// <c>URLS</c> environment variable or appsettings key can never move a listener, let alone put the admin on
    /// plain HTTP. Listeners come only from <c>Paduraksa:Listen</c>. Must be the last configuration source.
    /// </summary>
    public static void PinListenerConfiguration(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        PinListenerConfiguration(builder.Configuration);
    }

    /// <inheritdoc cref="PinListenerConfiguration(IHostApplicationBuilder)"/>
    public static void PinListenerConfiguration(IConfigurationBuilder configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["urls"] = string.Empty,
            ["http_ports"] = string.Empty,
            ["https_ports"] = string.Empty,
            ["preferHostingUrls"] = "false",
        });
    }

    /// <summary>Lets callers (tests, later milestones) add services to one host; applied last.</summary>
    public static void ApplyCallerServices(IHostApplicationBuilder builder, HostContext context, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ApplyCallerServices(builder.Services, context, role);
    }

    /// <inheritdoc cref="ApplyCallerServices(IHostApplicationBuilder, HostContext, HostRole)"/>
    public static void ApplyCallerServices(IServiceCollection services, HostContext context, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        context.ConfigureServices?.Invoke(role, services);
    }

    /// <summary>Lifetime that doesn't listen to the console or signals; the orchestrator does.</summary>
    private sealed class PassiveHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
