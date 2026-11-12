using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Notifications;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Notifications;

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notification system: the dispatcher (also as <see cref="INotificationEvents"/>), the channel service, the upstream health
    /// board and its watcher, the certificate expiry monitor and the sign-in watch. Needs the config store, the certificate service, the
    /// <see cref="IPermissionEvaluator"/>, <see cref="ISecretStore"/>, <see cref="INotificationSecretSource"/>, <see cref="IMacKeySource"/> and
    /// <see cref="IUpstreamHealth"/>. The hosted services are started by the core host, not here.
    /// </summary>
    public static IServiceCollection AddPaduraksaNotifications(this IServiceCollection services, NotificationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new NotificationOptions());
        services.TryAddSingleton<INotificationSenders>(sp => new NotificationSenders(
            sp.GetRequiredService<INotificationSecretSource>(), sp.GetRequiredService<IMacKeySource>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<NotificationStats>();
        services.AddSingleton(sp => new NotificationDispatcher(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<INotificationSenders>(),
            sp.GetRequiredService<NotificationOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<NotificationDispatcher>(sp),
            sp.GetRequiredService<NotificationStats>()));
        services.AddSingleton<INotificationEvents>(sp => sp.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<UpstreamHealthBoard>();
        services.AddSingleton(sp => new HealthMonitor(
            sp.GetRequiredService<IUpstreamHealth>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<INotificationEvents>(),
            sp.GetRequiredService<UpstreamHealthBoard>(),
            sp.GetRequiredService<NotificationOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<HealthMonitor>(sp)));
        services.AddSingleton(sp => new CertificateExpiryMonitor(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<CertificateService>(),
            sp.GetRequiredService<INotificationEvents>(),
            sp.GetRequiredService<NotificationOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<CertificateExpiryMonitor>(sp)));
        services.AddSingleton(sp => new NotificationService(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<ConfigService>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<INotificationSenders>(),
            sp.GetRequiredService<NotificationOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger<NotificationService>(sp),
            sp.GetRequiredService<IPermissionEvaluator>(),
            sp.GetRequiredService<NotificationStats>()));
        services.AddSingleton(sp => new SignInWatch(
            sp.GetRequiredService<INotificationEvents>(), sp.GetRequiredService<NotificationOptions>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    private static ILogger<T> Logger<T>(IServiceProvider provider) =>
        (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<T>();
}
