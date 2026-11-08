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
    /// <summary>
    /// Set to <c>1</c> or <c>true</c> to print the full exception, stack trace included, when the server can't be
    /// built or disposed. By default that's one line on stderr. Read from the environment because it must work
    /// before (and when) the configuration itself fails to load.
    /// </summary>
    internal const string DebugVariable = "PADURAKSA_DEBUG";

    public static Task<int> InvokeAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

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

        root.Subcommands.Add(VaultCommands.Build());
        root.Subcommands.Add(IdentityCommands.BuildSetupToken());
        root.Subcommands.Add(IdentityCommands.BuildAdmin());
        root.Subcommands.Add(DoctorCommands.Build());

        // TODO(M10): config, backup, restore, migrate. The full doctor (ports, permissions, libmsquic, DNS, clock skew) is M11.
        return root;
    }

    /// <summary>
    /// Builds and runs the server. Failures that escape the host (it can't be built, or can't be disposed) become
    /// a short message on <paramref name="error"/> and an <see cref="ExitCodes"/> value, never an unhandled
    /// exception; <paramref name="includeStackTrace"/> adds the full exception.
    /// </summary>
    internal static async Task<int> ServeAsync(PaduraksaHostOptions? hostOptions, TextWriter error, bool includeStackTrace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);

        PaduraksaHost host;
        try
        {
            host = PaduraksaHost.Create(hostOptions);
        }
        catch (OptionsValidationException ex)
        {
            await error.WriteLineAsync("Invalid configuration:").ConfigureAwait(false);
            foreach (string failure in ex.Failures)
            {
                await error.WriteLineAsync("  " + failure).ConfigureAwait(false);
            }

            return ExitCodes.InvalidConfiguration;
        }
        catch (InvalidDataException ex)
        {
            // A configuration file that can't be parsed, e.g. malformed JSON in appsettings.json.
            await WriteErrorAsync(error, "Invalid configuration", ex, includeStackTrace).ConfigureAwait(false);
            return ExitCodes.InvalidConfiguration;
        }
#pragma warning disable CA1031 // Top level: anything else (unreadable config file, host build failure) is reported, not thrown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await WriteErrorAsync(error, "Paduraksa failed to start", ex, includeStackTrace).ConfigureAwait(false);
            return ExitCodes.StartupFailed;
        }

        int exitCode = ExitCodes.Success;
        try
        {
            await using (host.ConfigureAwait(false))
            {
                exitCode = await host.RunAsync(cancellationToken).ConfigureAwait(false);
            }

            return exitCode;
        }
#pragma warning disable CA1031 // RunAsync turns start/run failures into exit codes; only disposal can still throw here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await WriteErrorAsync(error, "Paduraksa failed to shut down cleanly", ex, includeStackTrace).ConfigureAwait(false);
            // A run that already failed keeps its own code (e.g. 2 for a failed start); a clean one becomes a failure.
            return exitCode == ExitCodes.Success ? ExitCodes.Failure : exitCode;
        }
    }

    private static Task<int> ServeAsync(ParseResult parseResult, CancellationToken cancellationToken) =>
        ServeAsync(hostOptions: null, parseResult.InvocationConfiguration.Error, IsDebugEnabled(), cancellationToken);

    private static bool IsDebugEnabled() =>
        Environment.GetEnvironmentVariable(DebugVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>One line: the message of the exception and of each inner exception; or the full exception.</summary>
    private static Task WriteErrorAsync(TextWriter error, string what, Exception exception, bool includeStackTrace)
    {
        if (includeStackTrace)
        {
            return error.WriteLineAsync($"{what}: {exception}");
        }

        List<string> messages = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return error.WriteLineAsync($"{what}: {string.Join(" ---> ", messages)} (set {DebugVariable}=1 for the stack trace)");
    }
}
