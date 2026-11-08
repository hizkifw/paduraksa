using System.CommandLine;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Ids;
using Paduraksa.Data;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Vault;

namespace Paduraksa.Server.Cli;

/// <summary>
/// <c>paduraksa setup-token</c>, <c>paduraksa admin reset-password &lt;user&gt;</c> and <c>paduraksa admin disable-2fa &lt;user&gt;</c>
/// (PLAN §11.4, §12.1): what an operator with shell access does when the admin app can't be used (nobody can sign in yet, a password or
/// an authenticator is lost). They work on the data directory directly, so they run with or without the server, and every change is
/// audited under the <c>cli</c> actor.
/// </summary>
/// <remarks>
/// <b>A new password never comes through the command line</b> (argv shows in process listings and shell history): it is read from
/// standard input when that is piped (one line), or typed twice at a prompt without echo when it is a terminal.
/// </remarks>
internal static class IdentityCommands
{
    public static Command BuildSetupToken()
    {
        Command command = new("setup-token", "Show the one-time setup token that creates the first Owner (or say that setup is complete).");
        command.SetAction((parseResult, cancellationToken) => SetupTokenAsync(
            hostOptions: null, parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, cancellationToken));
        return command;
    }

    public static Command BuildAdmin()
    {
        Command admin = new("admin", "Recover admin access from the host.");

        Argument<string> resetUser = new("user") { Description = "The username." };
        Command reset = new("reset-password", "Set a user's password (read from standard input or prompted, never from the command line), end their sessions and clear a lockout.");
        reset.Arguments.Add(resetUser);
        reset.SetAction((parseResult, cancellationToken) => ResetPasswordAsync(
            hostOptions: null,
            parseResult.GetRequiredValue(resetUser),
            Console.In,
            standardInputIsTerminal: !Console.IsInputRedirected,
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));
        admin.Subcommands.Add(reset);

        Argument<string> disableUser = new("user") { Description = "The username." };
        Command disable = new("disable-2fa", "Turn off a user's second factor (a lost authenticator and recovery codes) and end their sessions.");
        disable.Arguments.Add(disableUser);
        disable.SetAction((parseResult, cancellationToken) => Disable2faAsync(
            hostOptions: null, parseResult.GetRequiredValue(disableUser), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, cancellationToken));
        admin.Subcommands.Add(disable);
        return admin;
    }

    /// <summary>Prints the setup token, or says setup is complete.</summary>
    internal static Task<int> SetupTokenAsync(PaduraksaHostOptions? hostOptions, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        RunAsync(hostOptions, error, async services =>
        {
            string? token = await services.Setup.EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(token is null ? "Setup is complete: there is no setup token." : $"Setup token: {token}").ConfigureAwait(false);
            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>Sets a user's password from standard input or a prompt.</summary>
    internal static Task<int> ResetPasswordAsync(
        PaduraksaHostOptions? hostOptions,
        string username,
        TextReader standardInput,
        bool standardInputIsTerminal,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        RunAsync(hostOptions, error, async services =>
        {
            string? password = standardInputIsTerminal
                ? await PromptAsync(output, error).ConfigureAwait(false)
                : (await standardInput.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.TrimEnd('\r');
            if (string.IsNullOrEmpty(password))
            {
                await error.WriteLineAsync("No password was given. Nothing changed.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            IdentityResult result = await services.Users.ResetPasswordAsync(CommandOrigin.Cli, username, password, cancellationToken).ConfigureAwait(false);
            switch (result.Status)
            {
                case IdentityStatus.Succeeded:
                    await output.WriteLineAsync($"Password reset for {username}: their sessions are ended and any lockout is cleared.").ConfigureAwait(false);
                    return ExitCodes.Success;
                case IdentityStatus.NotFound:
                    await error.WriteLineAsync($"There is no user {username}. Nothing changed.").ConfigureAwait(false);
                    return ExitCodes.Failure;
                default:
                    await error.WriteLineAsync($"{result.Problem ?? "The password was refused."} Nothing changed.").ConfigureAwait(false);
                    return ExitCodes.Failure;
            }
        }, cancellationToken);

    /// <summary>Turns off a user's second factor.</summary>
    internal static Task<int> Disable2faAsync(PaduraksaHostOptions? hostOptions, string username, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        RunAsync(hostOptions, error, async services =>
        {
            IdentityResult result = await services.TwoFactor.DisableForUserAsync(CommandOrigin.Cli, username, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                await output.WriteLineAsync($"The second factor of {username} is off and their sessions are ended.").ConfigureAwait(false);
                if (result.Problem is not null)
                {
                    // The change stands, but the vault could not delete the secret: say so, and exit non-zero so a script notices.
                    await error.WriteLineAsync("Warning: " + result.Problem).ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                return ExitCodes.Success;
            }

            await error.WriteLineAsync(result.Status == IdentityStatus.NotFound ? $"There is no user {username}. Nothing changed." : "Refused. Nothing changed.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }, cancellationToken);

    private sealed record Services(SetupService Setup, UserService Users, TwoFactorService TwoFactor);

    /// <summary>Loads the settings, brings the config database up to date and runs <paramref name="command"/> over the identity services.</summary>
    private static async Task<int> RunAsync(PaduraksaHostOptions? hostOptions, TextWriter error, Func<Services, Task<int>> command, CancellationToken cancellationToken)
    {
        PaduraksaOptions options;
        try
        {
            options = PaduraksaHost.LoadOptions(hostOptions);
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

        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            await using ServiceProvider provider = new ServiceCollection()
                .AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(options.DataDirectory, ConfigDatabase.FileName))
                .AddPaduraksaVault(options.DataDirectory)
                .AddPaduraksaIdentity(options.DataDirectory, options.Admin.ToIdentityOptions())
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            await ConfigDatabase.InitializeAsync(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>(), Int64OrderedIdGenerator.Shared, cancellationToken).ConfigureAwait(false);
            return await command(new Services(
                provider.GetRequiredService<SetupService>(), provider.GetRequiredService<UserService>(), provider.GetRequiredService<TwoFactorService>())).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            await error.WriteLineAsync($"Couldn't open the database: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Failed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    /// <summary>Reads a password twice from the terminal without echoing it; null if the two differ.</summary>
    private static async Task<string?> PromptAsync(TextWriter output, TextWriter error)
    {
        await output.WriteAsync("New password: ").ConfigureAwait(false);
        string first = ReadHidden();
        await output.WriteAsync("Repeat it: ").ConfigureAwait(false);
        string second = ReadHidden();
        if (first == second)
        {
            return first;
        }

        await error.WriteLineAsync("The two passwords differ.").ConfigureAwait(false);
        return null;
    }

    private static string ReadHidden()
    {
        StringBuilder text = new();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return text.ToString();
    }
}
