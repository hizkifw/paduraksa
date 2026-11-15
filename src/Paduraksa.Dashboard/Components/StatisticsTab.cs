using Microsoft.AspNetCore.Components;

using Paduraksa.Contracts.Metrics;

namespace Paduraksa.Dashboard.Components;

/// <summary>
/// What the statistics tabs of the Analytics page share: the host page owns the range and whether the tab is visible, and the tab reads,
/// polls and draws. A read starts when the tab is first shown to someone allowed to see it, starts over when what is asked for changes, and
/// repeats while the tab is visible; the hour is read every poll, a longer range every <see cref="LongRangeEvery"/>th (its figures are mostly
/// stored rows that change once a minute). A failed read stops the polling until "Retry", and a tab shown again reads at once.
/// </summary>
/// <typeparam name="TKey">Everything a read depends on besides time (the range, a host): a change of it drops the figures and reads again.</typeparam>
/// <typeparam name="TOverview">What one read returns.</typeparam>
public abstract class StatisticsTab<TKey, TOverview> : ComponentBase, IAsyncDisposable
    where TKey : struct
    where TOverview : class
{
    private TKey? _loaded;
    private bool _wasVisible = true;
    private bool _started;
    private CancellationTokenSource? _stop;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    /// <summary>
    /// Show the frame and read nothing: the page is still prerendering or checking who is asking, and a read made for someone who may not see
    /// the figures must not start. The frame is what the loaded tab looks like, so the page doesn't change size when this turns false.
    /// </summary>
    [Parameter]
    public bool Hold { get; set; }

    /// <summary>The range to read; the host page's picker.</summary>
    [Parameter, EditorRequired]
    public MetricsRange Range { get; set; }

    /// <summary>Whether the tab is visible: the host page watches it, and polling pauses while it is hidden.</summary>
    [Parameter]
    public bool Visible { get; set; } = true;

    /// <summary>How often the hour is read while the tab is visible; zero is <see cref="DefaultPollInterval"/>. A test shortens it; the person never changes it.</summary>
    [Parameter]
    public TimeSpan PollInterval { get; set; }

    /// <summary>The last figures read for what is asked now; null until the first read lands, and again while a changed request is read.</summary>
    protected TOverview? Overview { get; private set; }

    /// <summary>The last read failed: the tab shows its alert and stops polling until a read succeeds.</summary>
    protected bool Failed { get; private set; }

    /// <summary>The person may not read these figures: nothing is read, and nothing is asked again.</summary>
    protected bool Denied { get; set; }

    /// <summary>What a read depends on now, apart from time.</summary>
    protected abstract TKey CurrentKey { get; }

    protected abstract TimeSpan DefaultPollInterval { get; }

    /// <summary>A longer range is read on every this-many polls.</summary>
    protected abstract int LongRangeEvery { get; }

    protected abstract Task<TOverview> ReadAsync(TKey key, CancellationToken cancellationToken);

    /// <summary>Derives what the markup draws from a read that is still what is asked for.</summary>
    protected abstract void Shape(TOverview overview);

    /// <summary>Runs once before the first read: false does not start (the person isn't known yet, or <see cref="Denied"/>), and is asked again.</summary>
    protected virtual Task<bool> BeginAsync() => Task.FromResult(true);

    protected override async Task OnInitializedAsync()
    {
        if (RendererInfo.IsInteractive && !Hold)
        {
            await StartAsync();
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_started)
        {
            if (Hold || Denied || !RendererInfo.IsInteractive)
            {
                return;
            }

            await StartAsync();
            if (!_started)
            {
                return;
            }
        }

        // A new request starts over; a tab shown again reads at once.
        if (!IsLoaded(CurrentKey))
        {
            Overview = null;
            await LoadAsync();
        }
        else if (Visible && !_wasVisible)
        {
            await LoadAsync();
        }

        _wasVisible = Visible;
    }

    private async Task StartAsync()
    {
        if (!await BeginAsync())
        {
            return;
        }

        _stop = new CancellationTokenSource();
        _started = true;
        _ = PollAsync(_stop);
    }

    private bool IsLoaded(TKey key) => _loaded is { } loaded && EqualityComparer<TKey>.Default.Equals(loaded, key);

    protected async Task LoadAsync()
    {
        var key = CurrentKey;
        try
        {
            var overview = await ReadAsync(key, _stop?.Token ?? CancellationToken.None);

            // Asked for something else meanwhile: the read for that owns the figures.
            if (!EqualityComparer<TKey>.Default.Equals(key, CurrentKey))
            {
                return;
            }

            Overview = overview;
            Failed = false;
            Shape(overview);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed = true;
        }

        _loaded = key;
        StateHasChanged();
    }

    private async Task PollAsync(CancellationTokenSource stop)
    {
        var token = stop.Token;
        try
        {
            using PeriodicTimer timer = new(PollInterval > TimeSpan.Zero ? PollInterval : DefaultPollInterval, Time);
            var ticks = 0;
            while (await timer.WaitForNextTickAsync(token))
            {
                ticks++;
                if (Visible && !Failed && Overview is not null && (Range == MetricsRange.Hour || ticks % LongRangeEvery == 0))
                {
                    await InvokeAsync(LoadAsync);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The tab is gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is { } stop)
        {
            await stop.CancelAsync();
            stop.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
