using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// Compiles a validated config document into an unbound <see cref="RuntimeSnapshot"/> candidate (PLAN §6.1 step 4,
/// §6.4): resolves the settings layers into the proxy's compiler input and runs the snapshot compiler. Cold path.
/// </summary>
/// <remarks>The command pipeline runs <see cref="Config.ConfigValidator"/> first; the compiler is the backstop. The
/// implementation is <see cref="ConfigCompiler"/> (the mapping itself: <see cref="ProxyConfigMapping"/>).</remarks>
public interface IConfigCompiler
{
    /// <summary>Compiles <paramref name="document"/>, labelled with <paramref name="revision"/>.</summary>
    ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision);
}

/// <summary>Outcome of <see cref="IConfigCompiler.Compile"/>: an unbound candidate, or every error found.</summary>
/// <param name="Snapshot">The candidate, or <see langword="null"/> when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Errors with JSON-pointer paths into the document where the compiler can tell (PLAN §6.4).</param>
public sealed record ConfigCompilation(RuntimeSnapshot? Snapshot, IReadOnlyList<ConfigError> Errors)
{
    public bool Succeeded => Snapshot is not null;
}
