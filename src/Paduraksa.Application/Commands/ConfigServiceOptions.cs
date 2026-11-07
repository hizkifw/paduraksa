using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>Tunables of <see cref="ConfigService"/>.</summary>
public sealed class ConfigServiceOptions
{
    /// <summary>
    /// How long a command waits for its revision to go live before it returns
    /// <see cref="PublishOutcomeStatus.Pending"/> (a later outcome is still logged, and audited if it fails). The
    /// publisher has its own timeout for YARP (10 s); this one also covers the time before the proxy is attached,
    /// when a publish waits for the proxy to start.
    /// </summary>
    /// <remarks>
    /// A process that serves no proxy (the CLI writing to the database of a stopped server) has no live snapshot to
    /// update: give it a publisher that is never attached and <see cref="TimeSpan.Zero"/> here, so commands return at
    /// once with <see cref="PublishOutcomeStatus.Pending"/>; the server compiles the latest revision when it starts.
    /// </remarks>
    public TimeSpan PublishWait { get; init; } = TimeSpan.FromSeconds(15);
}
