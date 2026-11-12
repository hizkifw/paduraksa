using System.CommandLine;

using Paduraksa.Mcp;

namespace Paduraksa.Server.Cli;

/// <summary>
/// <c>paduraksa mcp-sandbox</c>: the child process the MCP tools run scripts in (PLAN §11.2). Not for people, so not in <c>--help</c>: the server starts it,
/// speaks length-prefixed JSON to it on stdin and stdout, and kills it on any breach. It reads no configuration and opens nothing of the server's.
/// </summary>
internal static class McpSandboxCommand
{
    public static Command Build()
    {
        Command command = new(SandboxChildCommand.Subcommand, "The sandbox process of the MCP tools (started by the server).") { Hidden = true };
        command.SetAction(_ => SandboxChild.Run(Console.OpenStandardInput(), Console.OpenStandardOutput()));
        return command;
    }
}
