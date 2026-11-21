using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Application.Compilation;

/// <summary>The prefix and the blog title a host's trap gets when its settings name none.</summary>
public sealed record HoneypotNames(string Prefix, string SiteName);

/// <summary>
/// What a host's trap is called when its settings leave the path and the name unset. Both come from a key only the compiler
/// holds, so the dashboard asks here instead of guessing: a derived prefix has to be shown, and linked, as it will be served.
/// </summary>
public sealed class HoneypotDerivation(SnapshotCompilerOptions options)
{
    /// <summary>The names of <paramref name="host"/>'s trap; null while the key they come from isn't open.</summary>
    public HoneypotNames? For(HostId host) =>
        SnapshotCompiler.TryDeriveHoneypotNames(options, host, out var prefix, out var siteName) ? new HoneypotNames(prefix, siteName) : null;
}
