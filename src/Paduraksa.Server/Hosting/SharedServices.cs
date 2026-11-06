using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The process-lifetime singletons shared by the core host, the public app and the admin app (PLAN §3).
/// <para>
/// Rules this type enforces:
/// <list type="bullet">
/// <item>Every shared service is <b>constructed here, exactly once</b>. <see cref="Builder.Add{TService}"/>
/// only accepts ready-made instances: there's no factory overload, so no container can ever build its own copy.</item>
/// <item><see cref="RegisterInto"/> registers those <b>same instances</b> into a container. Microsoft.Extensions.DependencyInjection
/// never disposes instances it didn't create, so disposing the three containers leaves them alone.</item>
/// <item><see cref="SharedServices"/> owns them: <see cref="DisposeAsync"/> disposes each one exactly once, in reverse
/// creation order, and only after every host has stopped (<see cref="PaduraksaHost"/> guarantees the ordering).
/// The logger factory is created first, so it's disposed last and shutdown logs still get flushed.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SharedServices : IAsyncDisposable
{
    private readonly IReadOnlyList<KeyValuePair<Type, object>> _registrations;
    private readonly IReadOnlyList<object> _owned;
    private int _disposed;

    private SharedServices(Builder builder, ILoggerFactory loggerFactory)
    {
        _registrations = [.. builder.Registrations];
        _owned = [.. builder.Owned];
        LoggerFactory = loggerFactory;
        TimeProvider = Get<TimeProvider>();
        Options = Get<PaduraksaOptions>();
        Health = Get<HealthState>();
        SnapshotStore = Get<SnapshotStore>();
        ServerCertificate = Get<PlaceholderServerCertificate>();
    }

    /// <summary>The one logger factory of the process. Each host sees it through a <see cref="RoleLoggerFactory"/>.</summary>
    public ILoggerFactory LoggerFactory { get; }

    public TimeProvider TimeProvider { get; }

    public PaduraksaOptions Options { get; }

    public HealthState Health { get; }

    public SnapshotStore SnapshotStore { get; }

    /// <summary>TEMPORARY (M3): shared by the public HTTPS and admin listeners until the vault exists.</summary>
    public PlaceholderServerCertificate ServerCertificate { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Builds every shared singleton. <paramref name="configure"/> may add more instances (tests, later milestones).</summary>
    public static SharedServices Create(IConfiguration configuration, PaduraksaOptions options, Action<Builder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        Builder builder = new();
        ILoggerFactory? loggerFactory = null;
        try
        {
            // Created first => disposed last.
            loggerFactory = builder.Own(CreateLoggerFactory(configuration));
            TimeProvider timeProvider = builder.Add(TimeProvider.System);
            // The validated options are the instance consumers get. IOptions/IOptionsMonitor/IOptionsSnapshot and
            // IOptionsFactory are pinned to it too; left alone they'd build their own unvalidated copies from
            // configuration (or unbound defaults).
            builder.Add(options);
            FixedOptions<PaduraksaOptions> fixedOptions = new(options);
            builder.Add<IOptions<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsMonitor<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsSnapshot<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsFactory<PaduraksaOptions>>(fixedOptions);
            builder.Add(new HealthState());
            builder.Add(new SnapshotStore(timeProvider));
            builder.Add(PlaceholderServerCertificate.Create(timeProvider));
            // TODO(M1): MetricsRegistry. TODO(M3): Vault capabilities, IAcmeChallengeStore.
            configure?.Invoke(builder);
            builder.Freeze();
            return new SharedServices(builder, loggerFactory);
        }
        catch
        {
            // The logger is created before the factory is disposed (last, in reverse creation order); the console
            // provider still writes synchronously after disposal, so a cleanup failure is reported, not lost.
            ILogger logger = loggerFactory is null ? NullLogger.Instance : loggerFactory.CreateLogger<SharedServices>();
            FailureCleanup.Run(() => DisposeAllAsync(builder.Owned).AsTask().GetAwaiter().GetResult(), logger, "disposing shared services");
            throw;
        }
    }

    /// <summary>
    /// Registers the shared instances into one host's container, replacing any default registration of the
    /// same service type, plus a per-host <see cref="ILoggerFactory"/> view tagging entries with the host's role.
    /// This is the <b>only</b> way shared services enter a container.
    /// </summary>
    public void RegisterInto(IServiceCollection services, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        foreach ((Type serviceType, object instance) in _registrations)
        {
            services.RemoveAll(serviceType);
            services.AddSingleton(serviceType, instance);
        }

        // Every host has its own LoggerFactory registration by default; swap it for a view of the shared one.
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(new RoleLoggerFactory(LoggerFactory, role));
    }

    /// <summary>Disposes every shared instance exactly once (idempotent, thread-safe).</summary>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) != 0 ? ValueTask.CompletedTask : DisposeAllAsync(_owned);

    private T Get<T>()
        where T : class =>
        (T)_registrations.First(static r => r.Key == typeof(T)).Value;

    private static ILoggerFactory CreateLoggerFactory(IConfiguration configuration) =>
        Microsoft.Extensions.Logging.LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddSimpleConsole(static console =>
            {
                console.SingleLine = true;
                console.IncludeScopes = true;
                console.UseUtcTimestamp = true;
                console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
                console.ColorBehavior = LoggerColorBehavior.Default;
            });
        });

    private static async ValueTask DisposeAllAsync(IReadOnlyList<object> owned)
    {
        List<Exception>? errors = null;
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            try
            {
                switch (owned[i])
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
#pragma warning disable CA1031 // Keep disposing the rest; failures are rethrown together below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("Disposing shared services failed.", errors);
        }
    }

    /// <summary>Collects shared instances. Instances only: there is deliberately no factory overload.</summary>
    internal sealed class Builder
    {
        private bool _frozen;

        internal List<KeyValuePair<Type, object>> Registrations { get; } = [];

        internal List<object> Owned { get; } = [];

        /// <summary>
        /// Adds <paramref name="instance"/> as the one <typeparamref name="TService"/> of the process.
        /// If it's disposable, <see cref="SharedServices"/> disposes it after every host has stopped.
        /// </summary>
        public TService Add<TService>(TService instance)
            where TService : class
        {
            ArgumentNullException.ThrowIfNull(instance);
            if (_frozen)
            {
                throw new InvalidOperationException("Shared services are frozen once the hosts are built.");
            }

            if (Registrations.Exists(r => r.Key == typeof(TService)))
            {
                throw new InvalidOperationException($"A shared {typeof(TService)} is already registered.");
            }

            Registrations.Add(new(typeof(TService), instance));
            Own(instance);
            return instance;
        }

        /// <summary>Takes ownership (disposal) without registering the instance as a service.</summary>
        internal T Own<T>(T instance)
            where T : class
        {
            if ((instance is IDisposable or IAsyncDisposable) && !Owned.Exists(o => ReferenceEquals(o, instance)))
            {
                Owned.Add(instance);
            }

            return instance;
        }

        internal void Freeze() => _frozen = true;
    }
}
