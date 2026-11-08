using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Data.Conversion;

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

    public static string CertificateSource(string column) => In(column, Enum.GetValues<CertificateSource>().Select(s => s.ToName()));

    public static string CertificateStatus(string column) => In(column, Enum.GetValues<CertificateStatus>().Select(s => s.ToName()));

    public static string Role(string column) => In(column, Enum.GetValues<Role>().Select(r => r.ToName()));

    public static string AcmeAccountStatus(string column) => In(column, Enum.GetValues<AcmeAccountStatus>().Select(s => s.ToName()));

    public static string AcmeOrderStatus(string column) => In(column, Enum.GetValues<AcmeOrderStatus>().Select(s => s.ToName()));

    public static string AcmeChallengeType(string column) => In(column, Enum.GetValues<AcmeChallengeType>().Select(t => t.ToName()));

    public static string DnsProviderKind(string column) => In(column, Enum.GetValues<DnsProviderKind>().Select(k => k.ToName()));

    public static string JwsAlgorithm(string column) => In(column, Enum.GetValues<JwsAlgorithm>().Select(a => a.ToName()));

    public static string KeySpec(string column) => In(column, Enum.GetValues<KeySpec>().Select(k => k.ToName()));

    private static string In(string column, IEnumerable<string> names) =>
        $"\"{column}\" IN ({string.Join(", ", names.Select(n => $"'{n}'"))})";
}
