using System.Net;

using Microsoft.Extensions.Options;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Server.Configuration;

/// <summary>
/// Startup validation for <see cref="PaduraksaOptions"/>. Runs once, before any listener is bound,
/// so a bad config fails fast with every problem listed instead of leaving a half-started process.
/// </summary>
/// <param name="allowEphemeralPorts">
/// Accept port 0 (OS-assigned). Only <see cref="Hosting.PaduraksaHostOptions.AllowEphemeralPorts"/> sets this;
/// it isn't reachable from configuration.
/// </param>
internal sealed class PaduraksaOptionsValidator(bool allowEphemeralPorts = false) : IValidateOptions<PaduraksaOptions>
{
    private const int MaxPort = 65535;

    public ValidateOptionsResult Validate(string? name, PaduraksaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add($"{PaduraksaOptions.SectionName}:DataDirectory must not be empty.");
        }
        else if (options.DataDirectory.StartsWith('~'))
        {
            // Nothing expands '~' here (that's the shell's job); it would silently become <content root>/~/...
            failures.Add($"{PaduraksaOptions.SectionName}:DataDirectory must not start with '~' (no home-directory expansion); use an absolute path, was '{options.DataDirectory}'.");
        }
        else if (options.DataDirectory.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:DataDirectory contains characters that aren't valid in a path.");
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:ShutdownTimeout must be positive.");
        }

        PaduraksaListenOptions listen = options.Listen;
        (string Name, int Port)[] ports =
        [
            ("Http", listen.Http),
            ("Https", listen.Https),
            ("Admin", listen.Admin),
        ];

        foreach ((string portName, int port) in ports)
        {
            int min = allowEphemeralPorts ? 0 : 1;
            if (port < min || port > MaxPort)
            {
                failures.Add($"{PaduraksaOptions.SectionName}:Listen:{portName} must be between {min} and {MaxPort}, was {port}.");
            }
        }

        // The admin listener may bind a different address than the public ones, but sharing a
        // port number between them is always a misconfiguration.
        IEnumerable<IGrouping<int, (string Name, int Port)>> clashes = ports
            .Where(static p => p.Port > 0)
            .GroupBy(static p => p.Port)
            .Where(static g => g.Count() > 1);
        foreach (IGrouping<int, (string Name, int Port)> clash in clashes)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen ports must be distinct; {string.Join(", ", clash.Select(static p => p.Name))} all use {clash.Key}.");
        }

        if (!IPAddress.TryParse(listen.AdminBind, out _))
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:AdminBind must be an IP address, was '{listen.AdminBind}'.");
        }

        if (listen.PublicHttpsPort is < 1 or > MaxPort)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:PublicHttpsPort must be between 1 and {MaxPort}, was {listen.PublicHttpsPort}.");
        }

        if (listen.Http3 && listen.Https == 0)
        {
            // Only reachable with ephemeral ports (tests): TCP and UDP must share one port, which 0 can't guarantee.
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:Http3 needs a fixed Https port (TCP and UDP share it); turn it off with an OS-assigned port.");
        }

        ValidateProxyProtocol("HttpProxyProtocol", listen.HttpProxyProtocol, failures);
        ValidateProxyProtocol("HttpsProxyProtocol", listen.HttpsProxyProtocol, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateProxyProtocol(string name, ProxyProtocolListenOptions? options, List<string> failures)
    {
        string key = $"{PaduraksaOptions.SectionName}:Listen:{name}";
        if (options is null)
        {
            failures.Add($"{key} must not be null.");
            return;
        }

        foreach (string source in options.TrustedSources)
        {
            if (!IpNetwork.TryParse(source ?? string.Empty, out _, out string? error))
            {
                failures.Add($"{key}:TrustedSources has '{source}', which isn't an IP address or CIDR ({error}).");
            }
        }

        if (options.Enabled && options.TrustedSources.Count == 0)
        {
            failures.Add($"{key}:TrustedSources must list the load balancers' addresses when PROXY protocol is enabled.");
        }

        if (options.HeaderTimeout <= TimeSpan.Zero || options.HeaderTimeout > ProxyProtocolOptions.MaxHeaderTimeout)
        {
            failures.Add($"{key}:HeaderTimeout must be positive and at most {ProxyProtocolOptions.MaxHeaderTimeout}, was {options.HeaderTimeout}.");
        }
    }
}
