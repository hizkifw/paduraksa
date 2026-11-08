using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Components;

namespace Paduraksa.Dashboard.Drawers;

/// <summary>How wide a drawer opens on a wide screen (every drawer takes the full width of a phone).</summary>
public enum DrawerWidth
{
    /// <summary>520 px: a short form (an ACME account, a DNS provider).</summary>
    Narrow,

    /// <summary>640 px: most forms, and "Review changes".</summary>
    Medium,

    /// <summary>780 px: the host form, whose routes sit side by side.</summary>
    Wide,
}

/// <summary>What a drawer handed back when it closed: a value, or nothing because it was cancelled.</summary>
/// <param name="Canceled">The drawer was closed without finishing (Cancel, Escape, the backdrop, a page that went away).</param>
/// <param name="Value">What it finished with (the upstream it made, the certificate it issued); null for a plain close.</param>
public sealed record DrawerResult(bool Canceled, object? Value)
{
    public static DrawerResult Cancel { get; } = new(true, null);

    public static DrawerResult Ok(object? value) => new(false, value);

    /// <summary>The value as <typeparamref name="T"/>; default when cancelled or of another type.</summary>
    public T? As<T>() => !Canceled && Value is T value ? value : default;
}

/// <summary>
/// The drawers open in a circuit, one above the other (PLAN §12): a form opens in a drawer, and a form it references (a new upstream from
/// the host form, a new certificate, an ACME account from the certificate form, "Review changes") stacks a drawer on top; finishing it
/// hands its result back to the level below and closes it. Only the top level is live: the ones below are dimmed, shifted aside and inert.
/// One per circuit; <see cref="DrawerHost"/> renders it.
/// </summary>
/// <remarks>
/// The component that pushes a level owns it: it awaits <see cref="DrawerLevel.Result"/> and cancels the level when it goes away itself
/// (a page cancels its drawers when it is disposed). Every call happens on the circuit's renderer thread.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a stack: levels are pushed and popped, last in, first out.")]
public sealed class DrawerStack
{
    private readonly List<DrawerLevel> _levels = [];
    private int _nextId;

    /// <summary>The open levels, bottom first.</summary>
    public IReadOnlyList<DrawerLevel> Levels => _levels;

    /// <summary>The level that takes input; null when no drawer is open.</summary>
    public DrawerLevel? Top => _levels.Count > 0 ? _levels[^1] : null;

    /// <summary>Whether any open level holds changes that closing would lose.</summary>
    public bool AnyDirty => _levels.Any(level => level.IsDirty);

    /// <summary>Whether leaving the page would lose something: unsaved changes, or what a level shows only once.</summary>
    public bool HoldsSomething => _levels.Any(level => level.IsDirty || level.LeaveWarning is not null);

    /// <summary>Raised after a level opened or closed, or a level's state the host shows changed (the discard question).</summary>
    public event Action? Changed;

    /// <summary>Opens <typeparamref name="TComponent"/> in a new level on top, with <paramref name="parameters"/>.</summary>
    public DrawerLevel Push<TComponent>(IReadOnlyDictionary<string, object?>? parameters = null, DrawerWidth width = DrawerWidth.Medium)
        where TComponent : IComponent =>
        Push(typeof(TComponent), parameters, width);

    /// <summary>Opens <paramref name="component"/> in a new level on top.</summary>
    public DrawerLevel Push(Type component, IReadOnlyDictionary<string, object?>? parameters = null, DrawerWidth width = DrawerWidth.Medium)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!typeof(IComponent).IsAssignableFrom(component))
        {
            throw new ArgumentException($"{component.Name} is not a component.", nameof(component));
        }

        DrawerLevel level = new(this, ++_nextId, component, parameters ?? new Dictionary<string, object?>(), width);
        _levels.Add(level);
        Changed?.Invoke();
        return level;
    }

    /// <summary>Opens a level and waits until it closes.</summary>
    public Task<DrawerResult> OpenAsync<TComponent>(IReadOnlyDictionary<string, object?>? parameters = null, DrawerWidth width = DrawerWidth.Medium)
        where TComponent : IComponent =>
        Push<TComponent>(parameters, width).Result;

    /// <summary>Escape: answers the top level's discard question with "keep editing", or asks the top level to close.</summary>
    public void Escape()
    {
        if (Top is not { } top)
        {
            return;
        }

        if (top.ConfirmingDiscard)
        {
            top.KeepEditing();
            return;
        }

        top.RequestClose();
    }

    /// <summary>Cancels every level, top first (the person chose to discard everything and leave).</summary>
    public void Clear()
    {
        if (_levels.Count > 0)
        {
            Remove(_levels[0], DrawerResult.Cancel);
        }
    }

    /// <summary>Closes <paramref name="level"/> with <paramref name="result"/>, cancelling every level above it first.</summary>
    internal void Remove(DrawerLevel level, DrawerResult result)
    {
        int index = _levels.IndexOf(level);
        if (index < 0)
        {
            return;
        }

        List<DrawerLevel> closing = _levels.GetRange(index, _levels.Count - index);
        _levels.RemoveRange(index, _levels.Count - index);
        for (int i = closing.Count - 1; i >= 0; i--)
        {
            closing[i].Complete(i == 0 ? result : closing[i].CancelResult);
        }

        Changed?.Invoke();
    }

    internal int DepthOf(DrawerLevel level) => _levels.IndexOf(level);

    internal void Notify() => Changed?.Invoke();
}

/// <summary>
/// One open drawer: what it shows and what it hands back. The drawer's component receives it as a cascading value, to close itself with a
/// result, to say it holds unsaved changes (so closing asks first), and to keep itself open while it applies.
/// </summary>
public sealed class DrawerLevel
{
    private readonly DrawerStack _stack;
    private readonly TaskCompletionSource<DrawerResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DrawerLevel(DrawerStack stack, int id, Type component, IReadOnlyDictionary<string, object?> parameters, DrawerWidth width)
    {
        _stack = stack;
        Id = id;
        Component = component;
        Parameters = parameters;
        Width = width;
    }

    /// <summary>Unique within the circuit; names the level's element and its heading.</summary>
    public int Id { get; }

    public Type Component { get; }

    public IReadOnlyDictionary<string, object?> Parameters { get; }

    public DrawerWidth Width { get; }

    /// <summary>What the drawer is about ("New host"), for the level above's way back and the dialog's name. Set by <c>DrawerShell</c>.</summary>
    public string? Title { get; set; }

    /// <summary>The drawer holds changes that closing would lose: Escape, the backdrop and the close button ask first.</summary>
    public bool IsDirty { get; set; }

    /// <summary>The page's address when this level opened at the bottom of the stack (set by <c>DrawerHost</c>).</summary>
    public string? OwnerPath { get; set; }

    /// <summary>The level has an address of its own (<see cref="RoutedDrawer"/>): the page opens and closes it as its address changes.</summary>
    public bool Routed { get; set; }

    /// <summary>
    /// What leaving the page would lose, in words, when it isn't an edit (a new token's secret, shown once): leaving asks first, with
    /// this. Null: nothing beyond <see cref="IsDirty"/>.
    /// </summary>
    public string? LeaveWarning { get; set; }

    /// <summary>The drawer is busy (an apply in flight): it can't be closed until it is done.</summary>
    public bool Busy { get; set; }

    /// <summary>
    /// What the level hands back however it closes from now on ("Review changes" after a commit that couldn't go live: closing it still
    /// tells the form the change was stored). Null: closing without finishing is a cancel.
    /// </summary>
    public object? ResultOnClose { get; set; }

    /// <summary>The level is waiting for an answer to "discard your changes?".</summary>
    public bool ConfirmingDiscard { get; private set; }

    public bool IsOpen => !_completion.Task.IsCompleted;

    public bool IsTop => ReferenceEquals(_stack.Top, this);

    /// <summary>The level's position, 0 at the bottom; -1 once closed.</summary>
    public int Depth => _stack.DepthOf(this);

    /// <summary>The level this one opened from; null at the bottom.</summary>
    public DrawerLevel? Parent => Depth is > 0 and var depth ? _stack.Levels[depth - 1] : null;

    /// <summary>Completes when the level closes, with what it handed back.</summary>
    public Task<DrawerResult> Result => _completion.Task;

    /// <summary>Finishes with <paramref name="value"/> (no question asked: the drawer did what it was for).</summary>
    public void Close(object? value = null)
    {
        IsDirty = false;
        _stack.Remove(this, DrawerResult.Ok(value));
    }

    /// <summary>Closes without a result and without asking (the owner went away, or the drawer itself decided).</summary>
    public void Cancel() => _stack.Remove(this, CancelResult);

    /// <summary>The person asked to close it (Escape, the backdrop, Cancel, the close button): asks first when it holds unsaved changes.</summary>
    public void RequestClose()
    {
        if (!IsOpen || Busy)
        {
            return;
        }

        if (IsDirty)
        {
            ConfirmingDiscard = true;
            _stack.Notify();
            return;
        }

        Cancel();
    }

    /// <summary>"Keep editing": the discard question goes away and the drawer stays.</summary>
    public void KeepEditing()
    {
        if (ConfirmingDiscard)
        {
            ConfirmingDiscard = false;
            _stack.Notify();
        }
    }

    /// <summary>"Discard": the changes are dropped and the drawer closes.</summary>
    public void Discard()
    {
        ConfirmingDiscard = false;
        IsDirty = false;
        Cancel();
    }

    internal DrawerResult CancelResult => ResultOnClose is { } value ? DrawerResult.Ok(value) : DrawerResult.Cancel;

    internal void Complete(DrawerResult result)
    {
        ConfirmingDiscard = false;
        _completion.TrySetResult(result);
    }
}
