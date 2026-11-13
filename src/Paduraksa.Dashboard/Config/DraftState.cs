using Microsoft.Extensions.Logging;

using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The caller's open draft as the dashboard's pieces see it, one per circuit: the draft bar, the forms' buttons and the pickers' "Draft" marks
/// read it, and it follows the draft (changed in this tab or another) and commits through the config feed. Only a user who may change config has a
/// draft; for anyone else it stays closed and asks the services nothing.
/// </summary>
public sealed class DraftState : IDisposable
{
    private readonly IDashboardConfig _config;
    private readonly DashboardCaller _caller;
    private readonly ILogger<DraftState> _logger;
    private IDisposable? _watch;
    private Task? _loading;

    public DraftState(IDashboardConfig config, DashboardCaller caller, ILogger<DraftState> logger)
    {
        _config = config;
        _caller = caller;
        _logger = logger;
    }

    /// <summary>Raised after the draft was read again and may have changed. Off the renderer's thread: a component hands it to <c>InvokeAsync</c>.</summary>
    public event Action? Changed;

    /// <summary>The open draft; null when there is none, or before <see cref="EnsureLoadedAsync"/> has read it.</summary>
    public ConfigDraft? Current { get; private set; }

    public bool IsOpen => Current is not null;

    /// <summary>The number of entries, blocked ones included.</summary>
    public int Count => Current?.Entries.Count ?? 0;

    /// <summary>How many entries are changed elsewhere: they stay out of an apply.</summary>
    public int BlockedCount => Current?.Entries.Count(static e => e.State == DraftEntryState.ChangedElsewhere) ?? 0;

    /// <summary>Whether the draft creates or changes the object <paramref name="target"/> (a <see cref="DraftTargets"/> value): a picker marks it "Draft".</summary>
    public bool IsDrafted(string target) =>
        Current?.Entries.Any(e => e.Target == target && e.Action != DraftEntryAction.Delete && e.State == DraftEntryState.Ready) == true;

    /// <summary>Reads the draft once, and keeps it current from then on. Safe to call from every component that needs it.</summary>
    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>Reads the draft again.</summary>
    public async Task RefreshAsync()
    {
        if (!await _caller.MayAsync(Permission.ConfigWrite).ConfigureAwait(false))
        {
            Set(null);
            return;
        }

        try
        {
            var result = await _config.GetDraftAsync().ConfigureAwait(false);
            Set(result.Status == DraftStatus.Succeeded ? result.Draft : null);
        }
        catch (Exception ex) when (StoreFailures.Survivable(ex))
        {
            _logger.LogStoreFailure(ex, "Couldn't read the draft.");
        }
    }

    /// <summary>Adds <paramref name="commands"/> to the draft, opening it first when there is none, and says how that went.</summary>
    public async Task<DraftResult> AddAsync(IReadOnlyList<ConfigCommand> commands)
    {
        var result = await _config.AddToDraftAsync(commands).ConfigureAwait(false);
        if (result.Status == DraftStatus.Succeeded)
        {
            Set(result.Draft);
        }

        return result;
    }

    /// <summary>Takes an entry out of the draft.</summary>
    public async Task<DraftResult> RemoveAsync(int number)
    {
        if (Current is not { } draft)
        {
            return new DraftResult(DraftStatus.NoDraft);
        }

        var result = await _config.RemoveDraftEntryAsync(draft.Id, number).ConfigureAwait(false);
        Take(result);
        return result;
    }

    /// <summary>Drops the draft.</summary>
    public async Task<DraftResult> DiscardAsync()
    {
        if (Current is not { } draft)
        {
            return new DraftResult(DraftStatus.NoDraft);
        }

        var result = await _config.DiscardDraftAsync(draft.Id).ConfigureAwait(false);
        if (result.Status is DraftStatus.Succeeded or DraftStatus.NoDraft)
        {
            Set(null);
        }

        return result;
    }

    /// <summary>What an apply left of the draft (the blocked entries), or nothing.</summary>
    public void Applied(ConfigDraft? remaining) => Set(remaining);

    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
        GC.SuppressFinalize(this);
    }

    private async Task LoadAsync()
    {
        _watch = _config.Watch(_ => RefreshAsync());
        await RefreshAsync().ConfigureAwait(false);
    }

    private void Take(DraftResult result)
    {
        if (result.Status == DraftStatus.Succeeded)
        {
            Set(result.Draft);
        }
        else if (result.Status == DraftStatus.NoDraft)
        {
            Set(null);
        }
    }

    private void Set(ConfigDraft? draft)
    {
        Current = draft;
        Changed?.Invoke();
    }
}
