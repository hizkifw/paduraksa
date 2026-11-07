using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// The <see cref="IConfigCompiler"/>: <see cref="ProxyConfigMapping.Map"/>, then <see cref="SnapshotCompiler.Compile"/>
/// with the process's <see cref="SnapshotCompilerOptions"/> (certificates, public HTTPS port, HTTP/3 advertising).
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

    public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision)
    {
        ArgumentNullException.ThrowIfNull(document);
        MappedConfig mapped = ProxyConfigMapping.Map(document, revision);
        CompileResult result = SnapshotCompiler.Compile(mapped.Model, _options);
        if (result.Snapshot is { } snapshot && mapped.Errors.Count == 0)
        {
            return new ConfigCompilation(snapshot, []);
        }

        CompileErrorPaths paths = new(document);
        return new ConfigCompilation(
            null,
            [
                .. mapped.Errors.Concat(result.Errors)
                    .Select(e => new ConfigError(paths.Of(e.Source), ConfigErrorCodes.CompileFailed, e.Message))
                    .Distinct(),
            ]);
    }
}
