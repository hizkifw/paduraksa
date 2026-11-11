using System.Text.Json.Nodes;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// An access list's passwords as a review or the history shows them: the users by name, and what is happening to each against
/// the other side of the change ("(new password)", "(password changed)", "(removed)"), never "Users: Username: alice" and never a
/// password or a credential ID. A typed password reaches a review as <see cref="ChangeView.PendingPassword"/> in the user's
/// <c>password</c>, so the word is all it says.
/// </summary>
internal static class AccessListUsers
{
    /// <summary>Whether a changed field of an access list (relative to it) is the passwords as a whole or the users among them.</summary>
    public static bool Handles(string relative) => relative is "/basicAuth" or "/basicAuth/users";

    /// <summary>The before and after text of a modified <c>/basicAuth</c> or <c>/basicAuth/users</c> field.</summary>
    public static (string? Before, string? After) Describe(string relative, JsonObject? oldList, JsonObject? newList)
    {
        var old = oldList?["basicAuth"] as JsonObject;
        var @new = newList?["basicAuth"] as JsonObject;
        string? Side(JsonObject? mine, JsonObject? other, bool isAfter) => relative == "/basicAuth"
            ? mine is null ? null : BasicAuth(mine, other ?? [], isAfter)
            : Users(mine?["users"] as JsonArray, other?["users"] as JsonArray ?? [], isAfter);

        var before = Side(old, @new, isAfter: false);
        var after = Side(@new, old, isAfter: true);

        // A password changed and nothing else did: the document names the same users, so the text alone would show no change.
        return before is not null && before == after ? (before, after + "; a password was changed") : (before, after);
    }

    /// <summary>The passwords object as one line: the realm, then the users. <paramref name="other"/> is the same object on the other
    /// side of the change, or null when there isn't one (an added or removed list), when no user is marked as removed or changed.</summary>
    public static string BasicAuth(JsonObject auth, JsonObject? other, bool isAfter)
    {
        List<string> parts = [];
        if (auth["realm"]?.ToString() is { Length: > 0 } realm)
        {
            parts.Add($"Realm: {realm}");
        }

        if (Users(auth["users"] as JsonArray, other is null ? null : other["users"] as JsonArray ?? [], isAfter) is { } users)
        {
            parts.Add(users);
        }

        return parts.Count == 0 ? "none" : string.Join(" · ", parts);
    }

    /// <summary>The users by name; null for none. Against <paramref name="other"/>, a user missing there is "(removed)" on the before
    /// side and, with a password being set, "(new password)" on the after side; one on both sides with a password being set is
    /// "(password changed)".</summary>
    public static string? Users(JsonArray? side, JsonArray? other, bool isAfter)
    {
        var names = (side ?? []).OfType<JsonObject>().Select(user =>
        {
            var name = user["username"]?.ToString() ?? "-";
            var inOther = other is not null && other.OfType<JsonObject>().Any(o => o["username"]?.ToString() == name);
            var marker = isAfter
                ? user.ContainsKey("password") ? (inOther ? " (password changed)" : " (new password)") : string.Empty
                : other is not null && !inOther ? " (removed)" : string.Empty;
            return name + marker;
        }).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }
}
