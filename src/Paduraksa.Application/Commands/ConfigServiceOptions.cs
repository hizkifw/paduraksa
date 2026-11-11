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
    public TimeSpan PublishWait { get; init; } = TimeSpan.FromSeconds(15);
}
