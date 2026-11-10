using Paduraksa.Application.Certificates;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
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
/// </remarks>
public sealed class ConfigCompiler : IConfigCompiler
{
    private readonly SnapshotCompilerOptions _options;

    public ConfigCompiler(SnapshotCompilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inventory);
        var mapped = ProxyConfigMapping.Map(document, revision, inventory);
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

        // The inventory's resolver (usable certificates only) replaces whatever the options carry.
        var result = SnapshotCompiler.Compile(mapped.Model, _options with { Certificates = inventory.Resolver });
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
