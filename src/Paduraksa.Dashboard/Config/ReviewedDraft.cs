using Paduraksa.Contracts.Commands;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The change last opened in the review drawer and not applied yet, per circuit: the simulator offers it as a target ("the change under
/// review", PLAN §7.7), so a request can be tried against it before it goes live. Applying or discarding it clears it.
/// </summary>
public sealed class ReviewedDraft
{
    /// <summary>The pending command; null when nothing is under review.</summary>
    public ConfigCommand? Command { get; private set; }

    /// <summary>What the change is, in words ("Edit app.example.com").</summary>
    public string? Title { get; private set; }

    /// <summary>A host name the change is about, to pre-fill the simulator's URL.</summary>
    public string? SampleHost { get; private set; }

    public void Set(ConfigCommand command, string title, string? sampleHost)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(title);
        Command = command with { DryRun = true };
        Title = title;
        SampleHost = sampleHost;
    }

    public void Clear()
    {
        Command = null;
        Title = null;
        SampleHost = null;
    }
}
