using System.CommandLine;

using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;

namespace Paduraksa.Server.Cli;

/// <summary>
/// The <c>paduraksa</c> command line (PLAN §11.4). <c>paduraksa</c> and <c>paduraksa serve</c> run the server.
/// Future commands (<c>setup-token</c>, <c>admin</c>, <c>config</c>, <c>vault</c>, <c>backup</c>/<c>restore</c>,
/// <c>doctor</c>, <c>migrate</c>) are added as subcommands in <see cref="BuildRootCommand"/>.
/// Server settings come from appsettings and environment variables (e.g. <c>Paduraksa__Listen__Https=8443</c>).
/// </summary>
internal static class PaduraksaCli
{
    public static Task<int> InvokeAsync(string[] args)
    {
        RootCommand root = BuildRootCommand();
        // The serve command handles SIGINT/SIGTERM itself (graceful, ordered shutdown), so System.CommandLine's
        // own termination handling (cancel + hard exit after a timeout) is turned off.
        InvocationConfiguration invocation = new() { ProcessTerminationTimeout = null };
        return root.Parse(args).InvokeAsync(invocation);
    }

    internal static RootCommand BuildRootCommand()
    {
        RootCommand root = new("Paduraksa: a reverse proxy with a built-in WAF and admin UI.");

        Command serve = new("serve", "Run the proxy and the admin UI (default).");
        serve.SetAction(ServeAsync);
        root.Subcommands.Add(serve);

        // `paduraksa` with no command == `paduraksa serve`.
        root.SetAction(ServeAsync);

        // TODO(M3/M4/M8): setup-token, admin, config, vault, backup, restore, doctor, migrate.
        return root;
    }

    private static async Task<int> ServeAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        PaduraksaHost host;
        try
        {
            host = PaduraksaHost.Create();
        }
        catch (OptionsValidationException ex)
        {
            await parseResult.InvocationConfiguration.Error.WriteLineAsync("Invalid configuration:").ConfigureAwait(false);
            foreach (string failure in ex.Failures)
            {
                await parseResult.InvocationConfiguration.Error.WriteLineAsync("  " + failure).ConfigureAwait(false);
            }

            return ExitCodes.InvalidConfiguration;
        }

        await using (host.ConfigureAwait(false))
        {
            return await host.RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
