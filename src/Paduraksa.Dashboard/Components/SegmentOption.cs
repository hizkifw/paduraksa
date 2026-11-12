namespace Paduraksa.Dashboard.Components;

/// <summary>One choice of a <see cref="SegmentedControl{T}"/>.</summary>
/// <param name="Value">What choosing it selects.</param>
/// <param name="Label">The short text on the segment; a compact control is written short ("24 h"), prose long.</param>
/// <param name="Icon">An optional leading icon (a MudBlazor icon string).</param>
/// <param name="Tooltip">The segment's <c>title</c>: what an abbreviated label stands for.</param>
/// <param name="Key">The segment's <c>data-value</c>, for tests; the lower-cased value when null.</param>
/// <param name="Count">A number shown after the label (a filter's matches); a zero reads muted.</param>
/// <param name="Tone">A status dot before the label (<c>success</c>, <c>warning</c>, <c>error</c>, <c>info</c>).</param>
public sealed record SegmentOption<T>(T Value, string Label, string? Icon = null, string? Tooltip = null, string? Key = null, int? Count = null, string? Tone = null);
