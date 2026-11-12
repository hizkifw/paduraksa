using System.Reflection;
using System.Runtime.InteropServices;

namespace Paduraksa.Mcp;

/// <summary>How the parent starts a child: the same binary, with the hidden subcommand <c>mcp-sandbox</c> (PLAN §11.2).</summary>
public sealed record SandboxChildCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public const string Subcommand = "mcp-sandbox";

    /// <summary>This process's own binary, or <c>dotnet &lt;its assembly&gt;</c> when it was started that way.</summary>
    public static SandboxChildCommand ForThisServer()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return ForAssembly(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("The entry assembly is unknown."));
        }

        return new SandboxChildCommand(path, [Subcommand]);
    }

    /// <summary><c>dotnet &lt;assembly&gt; mcp-sandbox</c>, with the <c>dotnet</c> of the runtime this process runs on.</summary>
    public static SandboxChildCommand ForAssembly(string assemblyPath) =>
        new(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet")), [assemblyPath, Subcommand]);
}
