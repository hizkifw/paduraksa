using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Identity;

namespace Paduraksa.Dashboard.Editors;

/// <summary>
/// The shared life of the three draft drawers: the caller and its permissions, the document they edit,
/// inline validation, the dirty guard, review-before-apply, "changed elsewhere" and the reload that starts over. A drawer
/// supplies what makes it a host, an upstream or a profile through the abstract hooks.
/// <para>A base class rather than a helper the drawers compose: the state is the component's own (the drawer's level
/// reads its dirty flag, and review closes it with the result), and a hook per difference keeps each drawer's markup
/// and its few real differences visible.</para>
/// </summary>
public abstract class DraftDrawer<TModel, TConfig> : ComponentBase, IDisposable
    where TModel : class
    where TConfig : class
{
    private ILogger? _log;

    [Inject]
    protected IDashboardConfig Config { get; set; } = default!;

    [Inject]
    protected DashboardCaller Caller { get; set; } = default!;

    [Inject]
    protected DrawerStack Drawers { get; set; } = default!;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    protected ILoggerFactory Loggers { get; set; } = default!;

    [Inject]
    protected DraftState Drafts { get; set; } = default!;

    [CascadingParameter]
    protected DrawerLevel? Level { get; set; }

    /// <summary>Hand the object back instead of applying it (opened from a host's form, which applies it with the host).</summary>
    [Parameter]
    public bool Stage { get; set; }

    /// <summary>With <see cref="Stage"/>: an object made earlier and not applied yet, to change (null: a new one).</summary>
    [Parameter]
    public TConfig? Staged { get; set; }

    /// <summary>Errors about <see cref="Staged"/> (relative to it) from a review that refused it, shown at its fields at once.</summary>
    [Parameter]
    public IReadOnlyDictionary<string, string>? Errors { get; set; }

    /// <summary>With <see cref="Stage"/>: the host form's draft, which this object is checked against.</summary>
    [Parameter]
    public ConfigDocument? Draft { get; set; }

    protected TModel? Model { get; set; }

    protected ConfigDocument? Document { get; set; }

    protected IReadOnlyDictionary<string, string> FieldErrors { get; set; } = new Dictionary<string, string>();

    protected HashSet<string> Touched { get; } = new(StringComparer.Ordinal);

    protected bool Attempted { get; set; }

    protected bool MayWrite { get; set; }

    protected bool Missing { get; set; }

    protected bool Denied { get; set; }

    protected bool Failed { get; set; }

    protected bool Reviewing { get; set; }

    protected string StoredName { get; set; } = string.Empty;

    protected string Opened { get; set; } = string.Empty;

    protected string? DeleteRefusal { get; set; }

    protected RevisionRef? ChangedElsewhere { get; set; }

    protected IDisposable? Watch { get; private set; }

    protected ILogger Log => _log ??= Loggers.CreateLogger(GetType());

    protected abstract string LoadFailureMessage { get; }

    protected abstract string RefreshFailureMessage { get; }

    protected abstract Task<ConfigDocument> LoadSourceAsync();

    /// <summary>Finds the object in <paramref name="document"/>, or starts a new one; false when it isn't there (<see cref="Missing"/> is set).</summary>
    protected abstract bool LoadModel(ConfigDocument document);

    /// <summary>The object this drawer would send: the command's content, and what the fingerprint hashes.</summary>
    protected abstract TConfig ToConfig();

    /// <summary>What the drawer has that makes it dirty, for the leave guard.</summary>
    protected abstract string Fingerprint();

    /// <summary>The drawer's own validation, into <see cref="FieldErrors"/>.</summary>
    protected abstract void Validate();

    /// <summary>Whether someone else's commit changed the object this drawer is editing.</summary>
    protected abstract bool IsChangedElsewhere(ConfigCommitted committed);

    /// <summary>Re-reads what a commit elsewhere changed, keeping the edits.</summary>
    protected abstract Task RefreshAsync();

    /// <summary>Whether a commit elsewhere re-reads the document here: a new object has no stored version to report.</summary>
    protected virtual bool TracksCommits => true;

    protected virtual void OnPrincipal(DashboardPrincipal user)
    {
    }

    protected virtual void OnReset()
    {
    }

    protected virtual void OnDocumentLoaded()
    {
    }

    protected virtual void OnReload()
    {
    }

    protected override async Task OnInitializedAsync()
    {
        if (!Stage)
        {
            Watch = Config.Watch(committed => InvokeAsync(() => OnCommittedAsync(committed)));
        }

        await LoadAsync();
    }

    public void Dispose()
    {
        Watch?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task LoadAsync()
    {
        ChangedElsewhere = null;
        Attempted = false;
        Failed = false;
        DeleteRefusal = null;
        Touched.Clear();
        OnReset();

        var user = await Caller.CheckAsync();
        if (user is null)
        {
            return;
        }

        if (!user.Has(Permission.ConfigRead))
        {
            Denied = true;
            return;
        }

        MayWrite = user.Has(Permission.ConfigWrite);
        OnPrincipal(user);
        try
        {
            Document = Stage && Draft is not null ? Draft : await LoadSourceAsync();
        }
        catch (Exception ex) when (StoreFailures.Survivable(ex))
        {
            Log.LogStoreFailure(ex, LoadFailureMessage);
            Failed = true;
            return;
        }

        OnDocumentLoaded();
        if (!LoadModel(Document))
        {
            return;
        }

        Opened = Fingerprint();
        Validate();
        if (Errors is { Count: > 0 } given)
        {
            MergeErrors(given);
        }
    }

    protected string? E(string path) =>
        (Attempted || Touched.Contains(path)) && FieldErrors.TryGetValue(path, out var message) ? message : null;

    protected void Set(Action apply, string path)
    {
        apply();
        Touch(path);
    }

    protected void Touch(string path)
    {
        Touched.Add(path);
        Validate();
        MarkDirty();
    }

    /// <summary>The form changed without naming a field (a nested form's result).</summary>
    protected void Changed()
    {
        Validate();
        MarkDirty();
    }

    /// <summary>Starts a submit when it may: submitted, validated, and nothing to fix.</summary>
    protected bool CanSubmit()
    {
        if (Model is null || !MayWrite || Reviewing)
        {
            return false;
        }

        Attempted = true;
        Validate();
        return FieldErrors.Count == 0;
    }

    /// <summary>Where this object's errors sit in the document, before its own path.</summary>
    protected virtual string ErrorPathPrefix => string.Empty;

    /// <summary>The dry run's errors about this object, at its fields.</summary>
    protected virtual void OnInvalid(IReadOnlyList<ConfigError> errors) => MergeErrors(InlineValidation.Relative(errors, ErrorPathPrefix));

    /// <summary>Adds what a review refused at its paths, keeping the errors already shown.</summary>
    protected void MergeErrors(IEnumerable<KeyValuePair<string, string>> found)
    {
        Dictionary<string, string> merged = new(FieldErrors, StringComparer.Ordinal);
        foreach (var (path, message) in found)
        {
            merged[path] = message;
        }

        FieldErrors = merged;
        Attempted = true;
    }

    /// <summary>What the form's submit does with its change: the default depends on whether a draft is open.</summary>
    private enum SubmitMode
    {
        Default,
        ApplyNow,
        AddToDraft,
    }

    private SubmitMode _mode;

    /// <summary>
    /// The form's change goes where the person asked: Enter and the primary button add it to the open draft, or review it when there is none;
    /// "Review and apply now" and "Add to a new draft" are the secondary ways (<see cref="ApplyNowAsync"/>, <see cref="AddToNewDraftAsync"/>).
    /// </summary>
    protected Task SubmitRequestAsync(ReviewRequest request, Func<Task>? onReload = null) =>
        _mode == SubmitMode.AddToDraft || (_mode == SubmitMode.Default && Drafts.IsOpen) ? AddToDraftAsync(request) : RunReviewAsync(request, onReload);

    /// <summary>A delete follows an open draft too: it becomes an entry (the object goes when the draft is applied), where a review would delete it now.</summary>
    protected Task DeleteRequestAsync(ReviewRequest request) => Drafts.IsOpen ? AddToDraftAsync(request) : RunReviewAsync(request);

    /// <summary>The form's own submit (validate, build the request, <see cref="SubmitRequestAsync"/>).</summary>
    protected abstract Task SubmitAsync();

    /// <summary>The secondary button of a form with a draft open: review this change on its own and apply it now.</summary>
    protected Task ApplyNowAsync() => SubmitAsAsync(SubmitMode.ApplyNow);

    /// <summary>The secondary button of a form with no draft open: start one with this change.</summary>
    protected Task AddToNewDraftAsync() => SubmitAsAsync(SubmitMode.AddToDraft);

    private async Task SubmitAsAsync(SubmitMode mode)
    {
        _mode = mode;
        try
        {
            await SubmitAsync();
        }
        finally
        {
            _mode = SubmitMode.Default;
        }
    }

    /// <summary>
    /// Adds the change to the draft (opening one when there is none), checked on the way in as a review would check it: a refusal is shown at
    /// the form's fields, a changed object as "changed elsewhere", and nothing is committed or audited until the draft is applied.
    /// </summary>
    protected async Task AddToDraftAsync(ReviewRequest request)
    {
        Reviewing = true;
        DraftResult result;
        try
        {
            result = await Drafts.AddAsync(request.DraftCommands ?? [request.Command]);
        }
        catch (Exception ex) when (StoreFailures.Survivable(ex))
        {
            Log.LogStoreFailure(ex, "Couldn't add a change to the draft.");
            Snackbar.Add(StoreFailures.Message("The change couldn't be added to the draft."), Severity.Error);
            return;
        }
        finally
        {
            Reviewing = false;
        }

        switch (result.Status)
        {
            case DraftStatus.Succeeded:
                Snackbar.Add($"Added to the draft: {request.Title}.", Severity.Success);
                Level?.Close(result.Draft);
                return;
            case DraftStatus.Denied:
                Snackbar.Add(IdentityText.CantChangeConfig, Severity.Warning);
                return;
            default:
                await ShowRefusalAsync(request, result);
                return;
        }
    }

    private async Task ShowRefusalAsync(ReviewRequest request, DraftResult result)
    {
        var command = result.Command;
        var commands = request.DraftCommands ?? [request.Command];
        switch (command?.Status)
        {
            case ConfigCommandStatus.Invalid when result.FailedCommand is null || result.FailedCommand == commands.Count - 1:
                await request.OnInvalid.InvokeAsync(command.Errors);
                Attempted = true;
                break;
            case ConfigCommandStatus.Invalid:
                Snackbar.Add($"Something made for this form needs a fix first: {(command.Errors.Count > 0 ? command.Errors[0].Message : null)}", Severity.Warning);
                break;
            case ConfigCommandStatus.Conflict:
                ChangedElsewhere = command.Conflict?.CurrentRevision ?? ChangedElsewhere;
                Snackbar.Add("This was changed elsewhere since you opened it. Reload to start from the new version.", Severity.Warning);
                break;
            case ConfigCommandStatus.NotFound:
                Snackbar.Add("It no longer exists, so there is nothing to change. Reload to see what is there now.", Severity.Warning);
                break;
            default:
                Snackbar.Add(command is { Errors.Count: > 0 } ? command.Errors[0].Message : "The change couldn't be added to the draft.", Severity.Error);
                break;
        }
    }

    protected async Task RunReviewAsync(ReviewRequest request, Func<Task>? onReload = null)
    {
        Reviewing = true;
        ReviewOutcome outcome;
        try
        {
            outcome = await Drawers.ReviewAsync(request);
        }
        finally
        {
            Reviewing = false;
        }

        if (outcome.Applied is { } applied)
        {
            await DropAppliedEntriesAsync(request);
            Level?.Close(applied);
        }
        else if (outcome.Reload)
        {
            await (onReload ?? ReloadAsync)();
        }
    }

    // What the draft held for an object that was just applied on its own is stale (its version moved): it goes, the commit having taken its place.
    private async Task DropAppliedEntriesAsync(ReviewRequest request)
    {
        if (Drafts.Current is not { } draft || request.Draft is not null)
        {
            return;
        }

        foreach (var command in request.DraftCommands ?? [request.Command])
        {
            if (DraftTargets.Of(command) is { } described && draft.Entries.FirstOrDefault(e => e.Target == described.Target) is { } entry)
            {
                await Drafts.RemoveAsync(entry.Number);
            }
        }
    }

    protected async Task ReloadAsync()
    {
        Model = null;
        OnReload();
        await LoadAsync();
        if (Level is not null)
        {
            Level.IsDirty = false;
        }
    }

    protected void Cancel() => Level?.RequestClose();

    private void MarkDirty()
    {
        if (Level is not null && Model is not null)
        {
            Level.IsDirty = Fingerprint() != Opened;
        }
    }

    private async Task OnCommittedAsync(ConfigCommitted committed)
    {
        if (!TracksCommits || Reviewing || Model is null)
        {
            return;
        }

        if (IsChangedElsewhere(committed))
        {
            ChangedElsewhere = committed.Revision;
        }

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex) when (StoreFailures.Survivable(ex))
        {
            Log.LogStoreFailure(ex, RefreshFailureMessage);
        }

        StateHasChanged();
    }
}
