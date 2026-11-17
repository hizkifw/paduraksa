using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.Components;

/// <summary>
/// What <see cref="SecurityObjectList{TItem}"/> needs to list one kind of named object that hosts and routes pick (an access list, a rate-limit
/// policy, an auth provider, a rule set): its words, its routes, its drawer and how a row reads. Test hooks derive from the nouns:
/// <c>data-</c> plus the noun in kebab case on a row, <c>add-</c> plus it on the add button, <c>pdk-</c> plus the plural plus <c>-table</c> on the table.
/// </summary>
public sealed record SecurityObjectKind<TItem>
{
    /// <summary>"access list".</summary>
    public required string Noun { get; init; }

    /// <summary>"access lists".</summary>
    public required string Plural { get; init; }

    public required string Icon { get; init; }

    /// <summary>The second column's heading: what a row says about the object ("Rules").</summary>
    public required string SummaryHeading { get; init; }

    public required string EmptyDescription { get; init; }

    /// <summary>The not-found drawer's link back to the list.</summary>
    public required string BackText { get; init; }

    public required string ListRoute { get; init; }

    public required string NewRoute { get; init; }

    /// <summary>The <c>data-section</c> of the list's card, where a kind has one.</summary>
    public string? Section { get; init; }

    public required Type Drawer { get; init; }

    /// <summary>The drawer's parameters for an object's key, or null when the key isn't an ID.</summary>
    public required Func<string, IReadOnlyDictionary<string, object?>?> DrawerParameters { get; init; }

    public required Func<ConfigDocument, IReadOnlyList<TItem>> Items { get; init; }

    public required Func<TItem, string> Name { get; init; }

    public required Func<TItem, string?> Notes { get; init; }

    public required Func<TItem, string> Route { get; init; }

    public required Func<TItem, string> DraftTarget { get; init; }

    public required Func<TItem, string> Summary { get; init; }

    public required Func<ConfigDocument, TItem, ReferenceUsage> Usage { get; init; }

    public required Func<TItem, ConfigCommand> Delete { get; init; }
}
