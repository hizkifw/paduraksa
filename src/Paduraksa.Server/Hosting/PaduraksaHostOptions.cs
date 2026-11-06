using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Knobs for <see cref="PaduraksaHost.Create"/>. The CLI passes none; tests use them to run fully
/// isolated hosts (own config, ephemeral ports, probes) without touching process-global state.
/// </summary>
internal sealed class PaduraksaHostOptions
{
    /// <summary>Defaults to <c>ASPNETCORE_ENVIRONMENT</c>, then <c>DOTNET_ENVIRONMENT</c>, then Production.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Where <c>appsettings*.json</c> are looked up. Defaults to the app's base directory, so a systemd unit or a
    /// shell started elsewhere still finds the shipped appsettings.
    /// </summary>
    public string? ContentRootPath { get; init; }

    /// <summary>Test-only: accept port 0 (OS-assigned) for the listeners. Deliberately not settable from configuration.</summary>
    public bool AllowEphemeralPorts { get; init; }

    /// <summary>Adds configuration sources after the defaults (appsettings, environment), so they win.</summary>
    public Action<IConfigurationBuilder>? ConfigureConfiguration { get; init; }

    /// <summary>Adds extra shared instances (registered into all three hosts, disposed once at the end).</summary>
    public Action<SharedServices.Builder>? ConfigureShared { get; init; }

    /// <summary>Adds to the process's one logger factory, e.g. a provider capturing log entries (tests).</summary>
    public Action<ILoggingBuilder>? ConfigureLogging { get; init; }

    /// <summary>Adds services to one host's container, after Paduraksa's own registrations.</summary>
    public Action<HostRole, IServiceCollection>? ConfigureServices { get; init; }
}
