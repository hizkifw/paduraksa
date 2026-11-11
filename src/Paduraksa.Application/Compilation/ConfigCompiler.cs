using Paduraksa.Application.Certificates;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// The <see cref="IConfigCompiler"/>: <see cref="ProxyConfigMapping.Map"/> (which resolves certificate bindings against
/// the inventory), then <see cref="SnapshotCompiler.Compile"/> with the process's <see cref="SnapshotCompilerOptions"/>
/// (public HTTPS port, HTTP/3 advertising) and the inventory's certificate resolver.
/// </summary>
/// <remarks>
/// Errors (the mapping's and the compiler's) come back as <see cref="ConfigErrorCodes.CompileFailed"/>, each with the
/// JSON pointer of the object it is about in the document as given (<see cref="CompileErrorPaths"/>). An inherited value
/// is reported where it is used: a bad address in a profile's allow list, say, at each host or route whose chain carries
/// it, since the compiler only sees the resolved settings. The same message at the same path is reported once.
/// <para>A user's password is the vault's: the mapping opens a handle for each credential, and a user whose credential the vault
/// holds no password for compiles, can't sign in and is a warning (<see cref="ConfigWarningCodes.AccessListUserNoPassword"/>), never an error.</para>
/// <para>The publish flag reaches <see cref="SnapshotCompilerOptions.RateLimitsWillBePublished"/>, so a draft, a dry run
/// or a simulation can't allocate a rate-limit slot for a pair no publish would ever retire.</para>
/// </remarks>
public sealed class ConfigCompiler : IConfigCompiler
{
    private readonly SnapshotCompilerOptions _options;
    private readonly IPasswordCredentials _credentials;

    public ConfigCompiler(SnapshotCompilerOptions options, IPasswordCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        _options = options;
        _credentials = credentials;
    }

    public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory) =>
        Compile(document, revision, inventory, publish: true);

    public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory, bool publish) =>
        Compile(document, revision, inventory, publish, PendingPasswords.None);

    public ConfigCompilation Compile(
        ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory, bool publish, PendingPasswords pendingPasswords)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(pendingPasswords);
        var mapped = ProxyConfigMapping.Map(document, revision, inventory, _credentials, pendingPasswords);
        List<MappedWarning> warnings = [.. mapped.Warnings];
        if (!_options.AdminUpstream)
        {
            foreach (var host in document.Hosts.Where(static h => h.Kind == HostKind.Admin))
            {
                warnings.Add(new MappedWarning(
                    CompileErrorSource.Host(host.Id),
                    string.Empty,
                    ConfigWarningCodes.AdminInternalDisabled,
                    "the admin host answers 503 until Paduraksa:Listen:AdminInternal is enabled"));
            }
        }

        if (document.Settings.TrustedProxies.Mode == TrustedProxyMode.CloudflareProxy
            && _options.CloudflareRanges?.Status().Warning is { } rangesWarning)
        {
            warnings.Add(new MappedWarning(CompileErrorSource.TrustedProxies, string.Empty, ConfigWarningCodes.CloudflareRangesStale, rangesWarning));
        }

        // The inventory's resolver (usable certificates only) replaces whatever the options carry.
        var result = SnapshotCompiler.Compile(
            mapped.Model,
            _options with { Certificates = inventory.Resolver, RateLimitsWillBePublished = publish });
        CompileErrorPaths paths = new(document);
        if (result.Snapshot is { } snapshot && mapped.Errors.Count == 0)
        {
            return new ConfigCompilation(
                snapshot,
                [],
                [
                    .. warnings
                        .Select(w => new ConfigWarning(paths.Of(w.Source) + w.Member, w.Code, w.Message))
                        .Distinct(),
                ])
            {
                ValidityChangesAt = inventory.NextBoundary,
            };
        }

        return new ConfigCompilation(
            null,
            [
                .. mapped.Errors.Concat(result.Errors)
                    .Select(e => new ConfigError(paths.Of(e.Source), ConfigErrorCodes.CompileFailed, e.Message))
                    .Distinct(),
            ]);
    }
}
