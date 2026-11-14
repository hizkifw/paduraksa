using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Paduraksa.Admin.Json;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Server.Cli;

/// <summary>Where the config commands send their requests and as whom.</summary>
/// <param name="Url">The admin API's base address; null: <c>PADURAKSA_URL</c>, else this installation's own admin listener on loopback.</param>
/// <param name="TokenFile">A file whose first line is an API token; null: <c>PADURAKSA_TOKEN</c>.</param>
/// <param name="HostOptions">For the default address: how the settings are loaded (tests).</param>
/// <param name="Environment">The environment the variables are read from; null: the process's.</param>
/// <param name="AllowInsecureHttp">Send the token over plain HTTP to an address that isn't loopback (<c>--allow-insecure-http</c>).</param>
internal sealed record ConfigClientOptions(
    Uri? Url = null, string? TokenFile = null, PaduraksaHostOptions? HostOptions = null, Func<string, string?>? Environment = null, bool AllowInsecureHttp = false);

/// <summary>
/// <c>paduraksa config export</c> and <c>paduraksa config import</c> (docs/design/admin-surfaces.md "CLI", docs/design/admin-surfaces.md "YAML import and export"). They are clients of the running instance's admin API, like any script:
/// the config database has one writer, the server (docs/design/config.md "Build decisions (M2)"), so the CLI never opens it to write, and everything an import does (validation, the lock-out guard,
/// the audit event with the token as actor, the single revision) is the pipeline's. Nothing here reads the data directory, so an instance that can't start has no
/// <c>config export</c>; its <c>last-good.json</c> and its backups hold the document.
/// </summary>
/// <remarks>
/// <para><b>The token</b> comes from <c>--token-file</c> or <c>PADURAKSA_TOKEN</c>, never from an argument (argv shows in process listings and shell history). It needs
/// <c>config:read</c> to export and <c>config:write</c> to import.</para>
/// <para><b>The certificate.</b> The admin listener serves the installation's own local CA's certificate, which this short-lived process doesn't load, so a loopback address is
/// not validated, exactly as <c>doctor --liveness</c> does it; any other address must present a certificate the system trusts. The request follows no redirect and uses no proxy,
/// so the token only goes where the address says. A bearer token is never sent over plain HTTP to an address that isn't loopback unless <c>--allow-insecure-http</c> says so.</para>
/// </remarks>
internal static class ConfigCommands
{
    internal const string UrlVariable = "PADURAKSA_URL";

    internal const string TokenVariable = "PADURAKSA_TOKEN";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    public static Command Build()
    {
        Command config = new("config", "Export and import the config as YAML, through the running instance's admin API.");

        Option<Uri> url = new("--url")
        {
            Description = $"The admin API's address (default: ${UrlVariable}, else this installation's own admin listener on loopback).",
            // System.CommandLine has no converter for Uri: without this, parsing accepted the value and reading it threw.
            CustomParser = static result =>
            {
                var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : string.Empty;
                if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
                {
                    return parsed;
                }

                result.AddError($"--url must be an absolute http:// or https:// address, such as https://127.0.0.1:9443 (got '{text}').");
                return null;
            },
        };
        Option<bool> allowInsecureHttp = new("--allow-insecure-http") { Description = "Send the token over plain http:// to an address that isn't loopback (refused by default: anyone on the path could read it)." };
        Option<string> tokenFile = new("--token-file") { Description = $"A file whose first line is an API token (default: the ${TokenVariable} variable). Needs config:read to export, config:write to import." };

        Command export = new("export", "Write the current config (or a past revision) as YAML to standard output or --output.");
        Option<string> revision = new("--revision") { Description = "Export this revision (rev_...) instead of the latest." };
        Option<string> output = new("--output", "-o") { Description = "Write to this file instead of standard output." };
        export.Options.Add(url);
        export.Options.Add(tokenFile);
        export.Options.Add(allowInsecureHttp);
        export.Options.Add(revision);
        export.Options.Add(output);
        export.SetAction((parseResult, cancellationToken) => ExportAsync(
            new ConfigClientOptions(parseResult.GetValue(url), parseResult.GetValue(tokenFile), AllowInsecureHttp: parseResult.GetValue(allowInsecureHttp)),
            parseResult.GetValue(revision),
            parseResult.GetValue(output),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));

        Command import = new("import", "Import a YAML config (a file, or - for standard input) as one revision.");
        Argument<string> file = new("file") { Description = "The YAML file, or - to read standard input." };
        Option<string> mode = new("--mode") { Description = "merge (default): add or update the objects in the YAML, delete nothing. replace: the config becomes the YAML, deleting what it leaves out.", DefaultValueFactory = static _ => "merge" };
        mode.AcceptOnlyFromAmong("merge", "replace");
        Option<bool> dryRun = new("--dry-run") { Description = "Check everything and show the changes and warnings, but commit nothing." };
        Option<string> message = new("--message", "-m") { Description = "The revision's note (at most 1000 characters)." };
        Option<string> baseRevision = new("--base-revision") { Description = "Refuse the import if this revision (rev_...) is no longer the latest." };
        Option<bool> force = new("--force") { Description = "Do the import even if it removes the admin's own way in through the proxy." };
        import.Arguments.Add(file);
        import.Options.Add(url);
        import.Options.Add(tokenFile);
        import.Options.Add(allowInsecureHttp);
        import.Options.Add(mode);
        import.Options.Add(dryRun);
        import.Options.Add(message);
        import.Options.Add(baseRevision);
        import.Options.Add(force);
        import.SetAction((parseResult, cancellationToken) => ImportAsync(
            new ConfigClientOptions(parseResult.GetValue(url), parseResult.GetValue(tokenFile), AllowInsecureHttp: parseResult.GetValue(allowInsecureHttp)),
            parseResult.GetRequiredValue(file),
            parseResult.GetRequiredValue(mode) == "replace" ? ConfigImportMode.Replace : ConfigImportMode.Merge,
            new ImportRequest(parseResult.GetValue(dryRun), parseResult.GetValue(message), parseResult.GetValue(baseRevision), parseResult.GetValue(force)),
            Console.In,
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));

        config.Subcommands.Add(export);
        config.Subcommands.Add(import);
        return config;
    }

    /// <summary>The request-shaping options of <c>config import</c>.</summary>
    internal sealed record ImportRequest(bool DryRun = false, string? Message = null, string? BaseRevision = null, bool Force = false);

    internal static async Task<int> ExportAsync(
        ConfigClientOptions client, string? revision, string? outputFile, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var (target, refusal) = await ResolveAsync(client, error, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return refusal;
        }

        using (target.Client)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, Endpoint(target.Base, "config/export" + (string.IsNullOrWhiteSpace(revision) ? string.Empty : "?revision=" + Uri.EscapeDataString(revision))));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
            var (response, text) = await SendAsync(target.Client, request, error, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return ExitCodes.Failure;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await ReportRefusalAsync(response, text!, error).ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                if (outputFile is null or "-")
                {
                    await output.WriteAsync(text).ConfigureAwait(false);
                    return ExitCodes.Success;
                }

                try
                {
                    await File.WriteAllTextAsync(outputFile, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await error.WriteLineAsync($"Couldn't write {outputFile}: {ex.Message}").ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                await output.WriteLineAsync($"Wrote {outputFile}.").ConfigureAwait(false);
                return ExitCodes.Success;
            }
        }
    }

    internal static async Task<int> ImportAsync(
        ConfigClientOptions client, string file, ConfigImportMode mode, ImportRequest options, TextReader standardInput, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var yaml = await ReadYamlAsync(file, standardInput, error, cancellationToken).ConfigureAwait(false);
        if (yaml is null)
        {
            return ExitCodes.Failure;
        }

        var (target, refusal) = await ResolveAsync(client, error, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return refusal;
        }

        using (target.Client)
        {
            List<string> query = [$"mode={(mode == ConfigImportMode.Replace ? "replace" : "merge")}"];
            if (options.DryRun)
            {
                query.Add("dryRun=true");
            }

            if (options.Force)
            {
                query.Add("force=true");
            }

            if (!string.IsNullOrWhiteSpace(options.Message))
            {
                query.Add("message=" + Uri.EscapeDataString(options.Message));
            }

            using HttpRequestMessage request = new(HttpMethod.Post, Endpoint(target.Base, "config/import?" + string.Join('&', query)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
            if (!string.IsNullOrWhiteSpace(options.BaseRevision))
            {
                request.Headers.TryAddWithoutValidation("If-Match", $"\"{options.BaseRevision.Trim('"')}\"");
            }

            request.Content = new ByteArrayContent(yaml);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/yaml") { CharSet = "utf-8" };
            var (response, text) = await SendAsync(target.Client, request, error, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return ExitCodes.Failure;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await ReportRefusalAsync(response, text!, error).ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                return await ReportResultAsync(text!, output, error).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> ReportResultAsync(string json, TextWriter output, TextWriter error)
    {
        ConfigCommandResult? result;
        try
        {
            result = JsonSerializer.Deserialize(json, AdminJsonContext.Default.ConfigCommandResult);
        }
        catch (JsonException)
        {
            result = null;
        }

        if (result is null)
        {
            await error.WriteLineAsync("The server's answer isn't a config command result.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        switch (result.Status)
        {
            case ConfigCommandStatus.Unchanged:
                await output.WriteLineAsync("Nothing to change: the config already is the YAML.").ConfigureAwait(false);
                break;
            case ConfigCommandStatus.DryRun:
                await output.WriteLineAsync($"Dry run: {result.Changes.Count} change(s), nothing committed.").ConfigureAwait(false);
                foreach (var change in result.Changes)
                {
                    await output.WriteLineAsync($"  {Symbol(change.Kind)} {change.Path}{(change.Fields.Count > 0 ? " (" + string.Join(", ", change.Fields) + ")" : string.Empty)}").ConfigureAwait(false);
                }

                break;
            default:
                await output.WriteLineAsync($"Imported as revision {result.Revision}: {result.Changes.Count} change(s).").ConfigureAwait(false);
                break;
        }

        foreach (var warning in result.Warnings)
        {
            await output.WriteLineAsync($"warning: {warning.Path}: {warning.Message}").ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    private static char Symbol(ConfigChangeKind kind) => kind switch
    {
        ConfigChangeKind.Added => '+',
        ConfigChangeKind.Removed => '-',
        _ => '~',
    };

    // A refusal is a problem document: the detail, then each error with the YAML path it points at.
    private static async Task ReportRefusalAsync(HttpResponseMessage response, string body, TextWriter error)
    {
        ApiProblem? problem = null;
        try
        {
            problem = JsonSerializer.Deserialize(body, AdminJsonContext.Default.ApiProblem);
        }
        catch (JsonException)
        {
            // Not a problem document (a proxy's page, say): the status says enough.
        }

        await error.WriteLineAsync($"Refused ({(int)response.StatusCode} {response.ReasonPhrase}): {problem?.Detail ?? problem?.Title ?? "the server gave no reason"}").ConfigureAwait(false);
        foreach (var item in problem?.Errors ?? [])
        {
            await error.WriteLineAsync($"  error {(item.Path.Length == 0 ? "(whole file)" : item.Path)}: {item.Message} [{item.Code}]").ConfigureAwait(false);
        }

        foreach (var item in problem?.RelatedErrors ?? [])
        {
            await error.WriteLineAsync($"  related {item.Path}: {item.Message} [{item.Code}]").ConfigureAwait(false);
        }
    }

    private static async Task<byte[]?> ReadYamlAsync(string file, TextReader standardInput, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            if (file == "-")
            {
                bytes = Encoding.UTF8.GetBytes(await standardInput.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }
            else
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > ConfigImportService.MaxBytes)
                {
                    await error.WriteLineAsync($"{file} is {info.Length} bytes; the largest YAML an import takes is {ConfigImportService.MaxBytes}.").ConfigureAwait(false);
                    return null;
                }

                bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (bytes.Length > ConfigImportService.MaxBytes)
            {
                await error.WriteLineAsync($"The YAML is {bytes.Length} bytes; the largest an import takes is {ConfigImportService.MaxBytes}.").ConfigureAwait(false);
                return null;
            }

            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Couldn't read {file}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private sealed record Target(HttpClient Client, Uri Base, string Token);

    /// <summary>The target, or null with the exit code to give: <see cref="ExitCodes.InvalidConfiguration"/> when the installation's own settings are invalid (as in every other command), else <see cref="ExitCodes.Failure"/>.</summary>
    private static async Task<(Target? Target, int ExitCode)> ResolveAsync(ConfigClientOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        var environment = options.Environment ?? System.Environment.GetEnvironmentVariable;
        var token = await ReadTokenAsync(options, environment, error, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return (null, ExitCodes.Failure);
        }

        var address = options.Url ?? (environment(UrlVariable) is { Length: > 0 } fromEnvironment && Uri.TryCreate(fromEnvironment, UriKind.Absolute, out var parsed) ? parsed : null);
        if (address is null)
        {
            try
            {
                address = OwnAdminAddress(PaduraksaHost.LoadOptions(options.HostOptions));
            }
            catch (OptionsValidationException ex)
            {
                await error.WriteLineAsync("Invalid configuration: " + string.Join("; ", ex.Failures)).ConfigureAwait(false);
                return (null, ExitCodes.InvalidConfiguration);
            }
        }

        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
        {
            await error.WriteLineAsync($"The address {address} must be http or https.").ConfigureAwait(false);
            return (null, ExitCodes.Failure);
        }

        if (address.Scheme == Uri.UriSchemeHttp && !IsLoopback(address) && !options.AllowInsecureHttp)
        {
            await error.WriteLineAsync($"Refusing to send the API token over plain HTTP to {address.Host}: use https://, or pass --allow-insecure-http if the path to it is trusted.").ConfigureAwait(false);
            return (null, ExitCodes.Failure);
        }

        return (new Target(CreateClient(address), address, token), ExitCodes.Success);
    }

    private static async Task<string?> ReadTokenAsync(ConfigClientOptions options, Func<string, string?> environment, TextWriter error, CancellationToken cancellationToken)
    {
        string? token;
        if (options.TokenFile is { Length: > 0 } file)
        {
            try
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
                {
                    await error.WriteLineAsync($"Warning: {file} can be read by other users. Restrict it (chmod 600).").ConfigureAwait(false);
                }

                token = (await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Couldn't read the token file {file}: {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }
        else
        {
            token = environment(TokenVariable)?.Trim();
        }

        if (string.IsNullOrEmpty(token))
        {
            await error.WriteLineAsync($"No API token: give --token-file, or set {TokenVariable}. Create one in the dashboard (Account, API tokens) with the config:read scope to export and config:write to import.").ConfigureAwait(false);
            return null;
        }

        return token;
    }

    // This installation's own admin listener: the loopback plain-HTTP fallback when it is on, else HTTPS to the admin port (the same candidates doctor --liveness probes).
    internal static Uri OwnAdminAddress(PaduraksaOptions options)
    {
        var health = DoctorCommands.HealthUris(options)[0];
        return new UriBuilder(health.Scheme, health.Host, health.Port, "/").Uri;
    }

    private static HttpClient CreateClient(Uri address)
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero,
        };

        if (address.Scheme == Uri.UriSchemeHttps)
        {
            handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            if (IsLoopback(address))
            {
                // See the remarks: the installation's own CA signs the admin certificate, and this process doesn't load it.
#pragma warning disable CA5359 // Do not disable certificate validation: deliberate, loopback-only, documented above.
                handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
            }
        }

        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    private static bool IsLoopback(Uri address) => address.IsLoopback || string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static Uri Endpoint(Uri address, string relative) => new(address, "/api/v1/" + relative);

    private static async Task<(HttpResponseMessage? Response, string? Text)> SendAsync(HttpClient client, HttpRequestMessage request, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return (response, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Couldn't reach {client.BaseAddress ?? request.RequestUri}: {ex.Message}. Is the server running, and is --url (or {UrlVariable}) its admin address?").ConfigureAwait(false);
            return (null, null);
        }
    }
}
