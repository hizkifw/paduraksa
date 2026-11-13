using Microsoft.AspNetCore.Components;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Dashboard.Drawers;

/// <summary>A change to review and apply, as a form hands it to "Review changes".</summary>
/// <param name="Title">What is being reviewed, in words ("New host app.example.com").</param>
/// <param name="Command">The command to run as a dry run, then to apply.</param>
public sealed record ReviewRequest(string Title, ConfigCommand Command)
{
    /// <summary>For a whole-document command: the document it leaves, to show the diff against.</summary>
    public ConfigDocument? AfterDocument { get; init; }

    public string ApplyLabel { get; init; } = "Apply now";

    /// <summary>Apply removes something: the button is red.</summary>
    public bool Destructive { get; init; }

    /// <summary>The toast's text after Apply ("Created app.example.com").</summary>
    public string SuccessMessage { get; init; } = "Change applied";

    /// <summary>A host name the change is about, to pre-fill the simulator.</summary>
    public string? SampleHost { get; init; }

    /// <summary>The dry run's errors about what was sent (object-relative paths), for the form below to show at its fields.</summary>
    public EventCallback<IReadOnlyList<ConfigError>> OnInvalid { get; init; }

    /// <summary>For a whole-document command: the dry run's errors (document paths), for the form below to place at its objects' fields.</summary>
    public EventCallback<IReadOnlyList<ConfigError>> OnInvalidDocument { get; init; }

    /// <summary>
    /// What Add to draft adds, in order, when it is not just <see cref="Command"/>: a host form's staged objects and then the host (the form's
    /// one whole-document command is how it applies them at once; a draft keeps each as its own entry). A whole-document command
    /// (rollback, import) has none and cannot be drafted.
    /// </summary>
    public IReadOnlyList<ConfigCommand>? DraftCommands { get; init; }
}

/// <summary>How a review ended: applied (live, or stored but not live), added to the draft, or the person asked to reload after a conflict.</summary>
/// <param name="Applied">The commit's result; null when nothing was applied.</param>
/// <param name="Reload">The person chose Reload after a conflict.</param>
/// <param name="Drafted">The draft the change went into; null when it did not.</param>
public sealed record ReviewOutcome(ConfigCommandResult? Applied, bool Reload, ConfigDraft? Drafted = null)
{
    public static ReviewOutcome Closed { get; } = new(null, false);
}

/// <summary>Opens "Review changes" as the stack's next level.</summary>
public static class DrawerReview
{
    /// <summary>Reviews <paramref name="request"/> in a drawer on top, and says how it ended.</summary>
    public static async Task<ReviewOutcome> ReviewAsync(this DrawerStack stack, ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);
        var result = await Open(stack, request).Result.ConfigureAwait(true);
        return result.As<ReviewOutcome>() ?? ReviewOutcome.Closed;
    }

    /// <summary>Pushes the review level (for a caller that keeps hold of it).</summary>
    public static DrawerLevel Open(DrawerStack stack, ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);
        return stack.Push<ReviewPanel>(new Dictionary<string, object?> { [nameof(ReviewPanel.Request)] = request }, DrawerWidth.Medium);
    }
}
