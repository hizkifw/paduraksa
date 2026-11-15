using System.Text.RegularExpressions;

namespace Paduraksa.Tests.Dashboard.Components;

// The part of the markup conventions about references: a field that names another configured resource links to it (docs/design/dashboard.md,
// "A reference is a link"). It is a file of its own so a branch that edits the other conventions doesn't touch it.
public sealed partial class DashboardMarkupConventionTests
{
    /// <summary>
    /// Pickers that choose another object and, on purpose, have no "open" button. Each has its reason; a new picker is not on this list, so
    /// it passes OpenHref (the object's page) or says here why it doesn't.
    /// </summary>
    private static readonly Dictionary<string, string> PickersWithoutAnOpenLink = new()
    {
        ["Certificates/AcmeRequestForm.razor"] = "a step of the add-certificate drawer: leaving it for the account's or provider's page drops the request; the choice is on the certificate's drawer afterwards",
        ["Components/RefPicker.razor"] = "the component itself: its callers pass OpenHref",
        ["Components/HostForm.razor#route-upstream"] = "the route's menu has Edit upstream, which stacks the upstream's drawer on the host's and keeps the form",
    };

    [Fact]
    public void EveryPickerOfAnotherObjectOffersItsPageOrSaysWhyNot()
    {
        List<string> without = [];
        foreach (var (path, text) in Markup())
        {
            foreach (Match picker in RefPickerElement().Matches(text))
            {
                var key = path.Replace("src/Paduraksa.Dashboard/", string.Empty, StringComparison.Ordinal);
                var routeUpstream = picker.Value.Contains("route-upstream-", StringComparison.Ordinal);
                if (picker.Value.Contains("OpenHref=\"", StringComparison.Ordinal)
                    || PickersWithoutAnOpenLink.ContainsKey(routeUpstream ? key + "#route-upstream" : key))
                {
                    continue;
                }

                without.Add($"{key}: {picker.Value.Split('\n')[0].Trim()}");
            }
        }

        Assert.True(without.Count == 0, "These pickers choose another object but give no way to open it (OpenHref=\"...\"), nor are listed with a reason:\n" + string.Join('\n', without));
    }

    [GeneratedRegex(@"<RefPicker\b.*?</RefPicker>", RegexOptions.Singleline)]
    private static partial Regex RefPickerElement();
}
