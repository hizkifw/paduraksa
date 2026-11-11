using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>An access list in the dashboard's words: one line for a list and the short phrase a picker's inherit choice
/// carries. The list is never named by ID.</summary>
public static class AccessListText
{
    /// <summary>The rules as a list row reads them: "2 allowed, 1 denied, only 2 countries, 3 users, address and password".</summary>
    public static string Summary(AccessListConfig list)
    {
        ArgumentNullException.ThrowIfNull(list);
        List<string> parts = [];
        var allow = list.Ip?.Allow.Count ?? 0;
        var deny = list.Ip?.Deny.Count ?? 0;
        var users = list.BasicAuth?.Users.Count ?? 0;
        var allowCountries = list.Ip?.Countries?.Allow.Count ?? 0;
        var denyCountries = list.Ip?.Countries?.Deny.Count ?? 0;
        if (allow > 0)
        {
            parts.Add($"{allow} allowed");
        }

        if (deny > 0)
        {
            parts.Add($"{deny} denied");
        }

        if (allowCountries > 0)
        {
            parts.Add($"only {allowCountries} {(allowCountries == 1 ? "country" : "countries")}");
        }

        if (denyCountries > 0)
        {
            parts.Add($"blocks {denyCountries} {(denyCountries == 1 ? "country" : "countries")}");
        }

        if (users > 0)
        {
            parts.Add($"{users} {(users == 1 ? "user" : "users")}");
        }

        if (users > 0 && (allow > 0 || deny > 0 || allowCountries > 0 || denyCountries > 0))
        {
            parts.Add(SatisfyText(list.Satisfy).ToLowerInvariant());
        }

        return parts.Count == 0 ? "no rules" : string.Join(", ", parts);
    }

    /// <summary>What <see cref="AccessListSatisfy"/> asks of a visitor, in the labels the editor offers.</summary>
    public static string SatisfyText(AccessListSatisfy satisfy) => satisfy == AccessListSatisfy.Any ? "Address or password" : "Address and password";

    /// <summary>What a layer would inherit, in the words of the picker's first choice: "no access list", or the list's name.
    /// A switch that is off is no list whatever is still remembered under it.</summary>
    public static string Inherited(AccessListSettings? inherited, IReadOnlyList<AccessListConfig> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (inherited is null || inherited.Enabled == false || inherited.AccessListId is not { IsEmpty: false } id)
        {
            return "no access list";
        }

        return lists.FirstOrDefault(list => list.Id == id)?.Name ?? "a list that isn't here";
    }
}
