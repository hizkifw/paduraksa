using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;

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
            Level?.Close(applied);
        }
        else if (outcome.Reload)
        {
            await (onReload ?? ReloadAsync)();
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
