namespace Paduraksa.Dashboard.Setup;

/// <summary>
/// Whether the person dismissed the overview's "Finish setting up" checklist in this browser. It is a cookie (like the theme and the time
/// zone) rather than local storage, so the server knows it on the first byte and renders the checklist, or leaves it out, in the prerender:
/// a checklist that appeared only once the circuit was live pushed the whole overview down by its height. Not a secret.
/// </summary>
public sealed class ChecklistPreference
{
    public const string Cookie = "paduraksa-checklist";

    public const string DismissedValue = "dismissed";

    public bool IsDismissed { get; private set; }

    /// <summary>Takes the request's cookie value; <c>Routes</c> hands it over, and the circuit keeps it.</summary>
    public void Initialize(string? cookieValue) => IsDismissed = cookieValue == DismissedValue;

    public void Dismiss() => IsDismissed = true;
}
