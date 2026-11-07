using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// Compiles a validated config document into an unbound <see cref="RuntimeSnapshot"/> candidate (PLAN §6.1 step 4,
/// §6.4): resolves the settings layers into the proxy's compiler input, resolves certificate bindings against the
/// certificate inventory (PLAN §9.2) and runs the snapshot compiler. Cold path, synchronous and pure: the inventory
/// carries everything the vault would have to be asked for.
/// </summary>
/// <remarks>The command pipeline runs <see cref="Config.ConfigValidator"/> first; the compiler is the backstop. The
/// implementation is <see cref="ConfigCompiler"/> (the mapping itself: <see cref="ProxyConfigMapping"/>).</remarks>
public interface IConfigCompiler
{
    /// <summary>Compiles <paramref name="document"/>, labelled with <paramref name="revision"/>, against <paramref name="inventory"/>.</summary>
    ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory);
}

/// <summary>Outcome of <see cref="IConfigCompiler.Compile"/>: an unbound candidate, or every error found.</summary>
/// <param name="Snapshot">The candidate, or <see langword="null"/> when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Errors with JSON-pointer paths into the document where the compiler can tell (PLAN §6.4).</param>
/// <param name="Warnings">Hosts without certificate coverage, bindings to unusable certificates: things that compile but
/// probably aren't intended (empty when the compile failed or nothing is off).</param>
public sealed record ConfigCompilation(RuntimeSnapshot? Snapshot, IReadOnlyList<ConfigError> Errors, IReadOnlyList<ConfigWarning>? Warnings = null)
{
    public IReadOnlyList<ConfigWarning> Warnings { get; init; } = Warnings ?? [];

    /// <summary>When the certificate inventory this was compiled against next changes with the clock
    /// (<see cref="CertificateInventory.NextBoundary"/>); null when it doesn't.</summary>
    public DateTimeOffset? ValidityChangesAt { get; init; }

    public bool Succeeded => Snapshot is not null;
}
