using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Compilation;

/// <summary>Compiles a validated config document into an unbound <see cref="RuntimeSnapshot"/> candidate: resolves
/// the settings layers into the proxy's compiler input, resolves certificate bindings against the certificate
/// inventory and runs the snapshot compiler.</summary>
/// <remarks>The command pipeline runs <see cref="Config.ConfigValidator"/> first; the compiler is the backstop. The
/// implementation is <see cref="ConfigCompiler"/> (the mapping itself: <see cref="ProxyConfigMapping"/>).</remarks>
public interface IConfigCompiler
{
    /// <summary>Compiles <paramref name="document"/>, labelled with <paramref name="revision"/>, against <paramref name="inventory"/>.</summary>
    ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory);

    /// <summary>
    /// <see cref="Compile(ConfigDocument, ConfigRevisionId, CertificateInventory)"/>, saying whether the candidate will
    /// be published. A draft, a dry run or a simulation passes false: such a compile must leave process-lifetime state
    /// (a rate-limit slot, say) exactly as it found it. Implementations that hold no such state can ignore it.
    /// </summary>
    ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory, bool publish) =>
        Compile(document, revision, inventory);

    /// <summary>
    /// <see cref="Compile(ConfigDocument, ConfigRevisionId, CertificateInventory, bool)"/> for a command whose passwords have not
    /// reached the vault yet (<see cref="PendingPasswords"/>): a user whose credential is in its assumed set is not reported as
    /// having no password, and one in its overlay compiles against the prepared handle. Implementations that don't look at passwords can ignore it.
    /// </summary>
    ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory, bool publish, PendingPasswords pendingPasswords) =>
        Compile(document, revision, inventory, publish);
}

/// <summary>Outcome of <see cref="IConfigCompiler.Compile(ConfigDocument, ConfigRevisionId, CertificateInventory)"/>: an unbound candidate, or every error found.</summary>
/// <param name="Snapshot">The candidate, or <see langword="null"/> when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Errors with JSON-pointer paths into the document where the compiler can tell.</param>
/// <param name="Warnings">Hosts without certificate coverage, bindings to unusable certificates: things that compile but
/// probably aren't intended (empty when the compile failed or nothing is off).</param>
public sealed record ConfigCompilation(RuntimeSnapshot? Snapshot, IReadOnlyList<ConfigError> Errors, IReadOnlyList<ConfigWarning>? Warnings = null)
{
    public IReadOnlyList<ConfigWarning> Warnings { get; init; } = Warnings ?? [];

    /// <summary>When the certificate inventory this was compiled against next changes with the clock
    /// (<see cref="CertificateInventory.NextBoundary"/>); null when it doesn't.</summary>
    public DateTimeOffset? ValidityChangesAt { get; init; }

    /// <summary>The compiled document's server settings: they take effect with the snapshot's publish (docs/design/config.md "Server settings").</summary>
    public ServerSettings? Server { get; init; }

    public bool Succeeded => Snapshot is not null;
}
