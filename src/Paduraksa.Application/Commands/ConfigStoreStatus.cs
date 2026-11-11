using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

public enum ConfigSource
{
    /// <summary><see cref="ConfigService.StartAsync"/> hasn't run (a process that only writes, like the CLI).</summary>
    NotStarted,

    /// <summary>The database's latest revision (or the empty config, before the first commit).</summary>
    Database,

    /// <summary>The last-known-good file: a degraded start.</summary>
    LastKnownGood,
}

/// <summary>The config store's state (<see cref="ConfigService.Status"/>).</summary>
/// <param name="Source">Where the running config came from.</param>
/// <param name="Revision">The revision startup published (null: the empty config, or not started), or the one whose
/// publish ended a degraded start.</param>
/// <param name="Problem">Why the start is degraded; null when it isn't.</param>
/// <param name="AcceptsCommands">False while the store itself is unusable: commands get
/// <see cref="ConfigCommandStatus.Unavailable"/>.</param>
public sealed record ConfigStoreStatus(ConfigSource Source, RevisionRef? Revision, string? Problem = null, bool AcceptsCommands = true)
{
    /// <summary>Before <see cref="ConfigService.StartAsync"/>.</summary>
    public static ConfigStoreStatus NotStarted { get; } = new(ConfigSource.NotStarted, null);

    /// <summary>A degraded start: not serving the database's latest config (see <see cref="Problem"/>).</summary>
    public bool IsDegraded => Problem is not null;
}

/// <summary>Startup found nothing to serve: neither the database's latest revision nor the last-known-good config can
/// be used (see <see cref="ConfigService.StartAsync"/>).</summary>
public sealed class ConfigStartupException : Exception
{
    public ConfigStartupException()
    {
    }

    public ConfigStartupException(string message)
        : base(message)
    {
    }

    public ConfigStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
