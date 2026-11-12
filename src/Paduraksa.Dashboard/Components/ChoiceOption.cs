namespace Paduraksa.Dashboard.Components;

/// <summary>One choice of <see cref="ChoiceCards{T}"/>.</summary>
/// <param name="Value">What choosing it selects.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Help">One line on what it means or does, under the title.</param>
/// <param name="Icon">An optional leading icon (a MudBlazor icon string).</param>
/// <param name="Key">The card's <c>data-value</c>, for tests; the lower-cased value when null.</param>
public sealed record ChoiceOption<T>(T Value, string Title, string? Help = null, string? Icon = null, string? Key = null);
