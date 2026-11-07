using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// TEMPORARY (M3): compiles a document that names no default certificate with the placeholder certificate
/// (<see cref="PlaceholderCertificates.Id"/>) as its default, so HTTPS answers for every host until the vault exists, as
/// it did in M1. Only the compiler's input changes: the stored document, its validation and its error paths don't.
/// <para>TODO(M3): when this goes away, stored documents that name no default certificate, or that name
/// <see cref="PlaceholderCertificates.Id"/> (the benchmark runner binds hosts to it), must still start: either the
/// compiler tolerates a missing default/placeholder certificate, or a migration rewrites those documents. Otherwise an
/// upgrade starts degraded (the latest revision doesn't compile) or not at all.</para>
/// </summary>
internal sealed class PlaceholderCertificateCompiler(IConfigCompiler inner) : IConfigCompiler
{
    public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision)
    {
        ArgumentNullException.ThrowIfNull(document);
        GlobalSettings settings = document.Settings;
        return inner.Compile(
            settings.DefaultCertificateId is null
                ? document with { Settings = settings with { DefaultCertificateId = PlaceholderCertificates.Id } }
                : document,
            revision);
    }
}
