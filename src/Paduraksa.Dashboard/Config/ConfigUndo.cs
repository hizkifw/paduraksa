using Microsoft.Extensions.Logging;

using MudBlazor;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The toast after a change is applied (PLAN §12): what was saved, as which revision, with <b>Undo</b>. Undo rolls the config back to the
/// revision before it through the rollback command (a new revision; history is never rewritten), based on the revision it undoes, so it
/// is refused when someone changed the config in between. One per circuit, so the toast's button outlives the page that applied.
/// </summary>
public sealed class ConfigUndo(IDashboardConfig config, DashboardCaller caller, ISnackbar snackbar, ILogger<ConfigUndo> logger)
{
    // Only the latest change can be undone (an undo is based on the revision it undoes), so a newer toast replaces the last one.
    private Snackbar? _offered;

    /// <summary>How long the toast with Undo stays up.</summary>
    public const int UndoVisibleMilliseconds = 12_000;

    /// <summary>Shows that <paramref name="what"/> went live as <paramref name="committed"/>, with Undo.</summary>
    public void Offer(RevisionRef committed, string what, bool pending = false)
    {
        ArgumentNullException.ThrowIfNull(committed);
        string message = pending
            ? $"{what}. Saved as {committed}; it goes live in a moment."
            : $"{what}. Live as {committed}.";
        if (_offered is { } previous)
        {
            snackbar.Remove(previous);
        }

        _offered = snackbar.Add(message, Severity.Success, options =>
        {
            options.Action = "Undo";
            options.ActionColor = Color.Inherit;
            options.ActionVariant = Variant.Outlined;
            options.VisibleStateDuration = UndoVisibleMilliseconds;
            options.OnClick = shown =>
            {
                // One click, one undo: the toast goes as the undo starts.
                snackbar.Remove(shown);
                return UndoAsync(committed);
            };
        });
    }

    /// <summary>Rolls back what <paramref name="committed"/> changed, and says how that went (null when it couldn't be tried).</summary>
    public async Task<ConfigCommandResult?> UndoAsync(RevisionRef committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        _offered = null;
        try
        {
            return await RollBackAsync(committed).ConfigureAwait(false);
        }
        catch (Exception ex) when (StoreFailures.Survivable(ex))
        {
            logger.LogStoreFailure(ex, $"Couldn't undo {committed}.");
            snackbar.Add($"Couldn't undo {committed}. Roll back from the history instead; the details are in the server log.", Severity.Error);
            return null;
        }
    }

    private async Task<ConfigCommandResult?> RollBackAsync(RevisionRef committed)
    {
        if (await caller.OriginAsync().ConfigureAwait(false) is not { } origin)
        {
            return null;
        }

        RevisionDetail? detail = await config.GetRevisionAsync(committed.Id).ConfigureAwait(false);
        string note = $"Undo {committed}";
        ConfigCommand command = detail?.Revision.ParentId is { } parent
            ? new RollbackConfigCommand(parent, committed.Id) { Message = note }
            : new ReplaceConfigCommand(ConfigService.InitialDocument, committed.Id) { Message = note };
        ConfigCommandResult result = await config.ExecuteAsync(command, origin).ConfigureAwait(false);
        switch (result.Status)
        {
            case ConfigCommandStatus.Committed:
                snackbar.Add($"Undone: {result.Revision} puts back the config from before {committed}.", Severity.Info);
                break;
            case ConfigCommandStatus.Unchanged:
                snackbar.Add($"Nothing to undo: the config is already as it was before {committed}.", Severity.Info);
                break;
            case ConfigCommandStatus.Conflict:
                snackbar.Add($"Can't undo {committed}: the config changed since. Roll back from the history instead.", Severity.Warning);
                break;
            case ConfigCommandStatus.PublishFailed:
                snackbar.Add($"The undo is stored as {result.Revision} but isn't live: publishing failed. See the server log.", Severity.Error, options => options.RequireInteraction = true);
                break;
            case ConfigCommandStatus.Denied:
                snackbar.Add("Your role can't change the config.", Severity.Error);
                break;
            default:
                string why = result.Errors.Count > 0 ? ReviewNotices.Sentence(result.Errors[0].Message) : "The config store refused it.";
                snackbar.Add($"Couldn't undo {committed}. {why}", Severity.Error);
                break;
        }

        return result;
    }
}
