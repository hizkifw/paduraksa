using Microsoft.AspNetCore.Components;

namespace Paduraksa.Dashboard.Drawers;

/// <summary>
/// A list page's drawer that has an address: <c>/hosts/host_...</c> is the hosts list with that host's drawer open, so links
/// from other pages, the audit log and the tests keep working. The page hands over its route's key on every parameter change; the key
/// opens the drawer it names (the page parses it, typed at the edge), no key closes it, and closing the drawer goes back to the list's
/// address. The page disposes it with itself, which closes the drawer (and every level stacked on it).
/// </summary>
public sealed class RoutedDrawer(NavigationManager navigation, string listAddress) : IDisposable
{
    private DrawerLevel? _root;

    /// <summary>The key whose drawer is open; null when none is.</summary>
    public string? Key { get; private set; }

    /// <summary>The open drawer; null when none is.</summary>
    public DrawerLevel? Level => _root;

    /// <summary>
    /// Shows the drawer <paramref name="key"/> names (through <paramref name="open"/>, which pushes it), unless it is open already; a null
    /// key closes the drawer (the person went back to the list's address).
    /// </summary>
    public void Show(string? key, Func<string, DrawerLevel> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (key is not null && key == Key && _root is { IsOpen: true })
        {
            return;
        }

        Close();
        if (key is null)
        {
            return;
        }

        DrawerLevel level = open(key);
        level.Routed = true;
        Key = key;
        _root = level;
        _ = FollowAsync(level);
    }

    public void Dispose() => Close();

    private void Close()
    {
        if (_root is { } root)
        {
            _root = null;
            Key = null;
            root.Cancel();
        }
    }

    private async Task FollowAsync(DrawerLevel level)
    {
        await level.Result.ConfigureAwait(true);
        if (ReferenceEquals(_root, level))
        {
            _root = null;
            Key = null;
            navigation.NavigateTo(listAddress, replace: true);
        }
    }
}
