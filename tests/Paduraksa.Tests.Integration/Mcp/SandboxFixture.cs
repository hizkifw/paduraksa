using System.Text;

using Paduraksa.Mcp;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>The sandbox as the server runs it: a pool of real child processes of the server's own assembly, over a small API description.</summary>
public sealed class SandboxFixture : IAsyncLifetime
{
    public CodeModeTools Tools { get; private set; } = null!;

    /// <summary>
    /// <c>dotnet paduraksa.dll mcp-sandbox</c> from the server project's own output folder: the test's copy of the assembly runs under the test's runtime
    /// configuration, which does not name the ASP.NET Core framework the server needs.
    /// </summary>
    public static SandboxChildCommand Command { get; } = SandboxChildCommand.ForAssembly(ServerAssembly());

    private static string ServerAssembly()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var root = output;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Paduraksa.slnx")))
        {
            root = root.Parent;
        }

        return Path.Combine(root!.FullName, "src", "Paduraksa.Server", "bin", output.Parent!.Name, output.Name, "paduraksa.dll");
    }

    public static CodeModeTools Create(SandboxLimits? limits = null, TimeSpan? idle = null, TimeProvider? time = null, Action<string>? log = null, SandboxChildCommand? command = null) =>
        CodeModeTools.Create(Encoding.UTF8.GetBytes(McpTestSpec.OpenApi), command ?? Command, idle ?? TimeSpan.FromMinutes(10), limits, time, log);

    public ValueTask InitializeAsync()
    {
        Tools = Create();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Tools.DisposeAsync();
}
