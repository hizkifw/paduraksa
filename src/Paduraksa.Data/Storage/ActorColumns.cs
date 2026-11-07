using Paduraksa.Core.Model;

namespace Paduraksa.Data.Storage;

/// <summary>
/// An <see cref="Actor"/> is stored as three columns: <c>ActorKind</c> (its <see cref="ModelNames"/> name),
/// <c>ActorUserId</c> and <c>ActorTokenId</c> (16-byte BLOBs). The IDs are deliberately not foreign keys: history
/// outlives the users and tokens it names.
/// </summary>
internal static class ActorColumns
{
    /// <summary>
    /// A check constraint: the kind is a known name, the user ID is set exactly for user actors and the token ID
    /// exactly for token actors.
    /// </summary>
    public static readonly string CheckSql =
        $"{NameChecks.ActorKind("ActorKind")} "
        + $"AND (\"ActorUserId\" IS NOT NULL) = (\"ActorKind\" = '{ActorKind.User.ToName()}') "
        + $"AND (\"ActorTokenId\" IS NOT NULL) = (\"ActorKind\" = '{ActorKind.Token.ToName()}')";
}

/// <summary>
/// Check constraints that keep enum columns to their <see cref="ModelNames"/> names, generated from the enums. A new
/// name changes the constraint, so it comes with a migration (in SQLite, a table rebuild).
/// </summary>
internal static class NameChecks
{
    public static string ActorKind(string column) => In(column, Enum.GetValues<ActorKind>().Select(k => k.ToName()));

    public static string Channel(string column) => In(column, Enum.GetValues<Channel>().Select(c => c.ToName()));

    public static string AuditOutcome(string column) => In(column, Enum.GetValues<AuditOutcome>().Select(o => o.ToName()));

    private static string In(string column, IEnumerable<string> names) =>
        $"\"{column}\" IN ({string.Join(", ", names.Select(n => $"'{n}'"))})";
}
