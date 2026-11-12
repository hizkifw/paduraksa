using System.Text;

using CsCheck;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Shared;
using Paduraksa.Tests.Unit.Compilation;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// The config pipeline over generated documents (<see cref="ConfigGen"/>): the validator never throws, a document that validates never fails to
/// compile, and the canonical JSON that is stored, hashed and read back at startup is idempotent, round-trips, and keeps a valid document valid.
/// </summary>
public sealed class ConfigPipelinePropertyTests : IDisposable
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);

    private readonly AnyCertificate _certificates = new();

    public void Dispose() => _certificates.Dispose();

    [Fact]
    public void ADocumentThatValidatesAlwaysCompilesAndNothingThrows()
    {
        var valid = 0;
        var total = 0;
        var withPages = 0;
        var withDocumentPages = 0;
        var withMaintenance = 0;
        var withBypass = 0;
        var withReplacement = 0;
        var refusedPages = 0;
        var refusedMaintenance = 0;
        ConfigGen.Documents.Check(
            500,
            document =>
            {
                total++;
                var errors = ConfigValidator.Validate(document);
                Assert.Equal(errors.Count, ConfigValidator.Validate(document).Count);
                refusedPages += errors.Any(static e => e.Path.Contains("/errorPages", StringComparison.Ordinal)) ? 1 : 0;
                refusedMaintenance += errors.Any(static e => e.Path.Contains("/maintenance", StringComparison.Ordinal)) ? 1 : 0;
                if (errors.Count > 0)
                {
                    return;
                }

                valid++;
                var layers = Layers(document);
                withPages += layers.Any(static l => l.ErrorPages?.Entries().Any(static e => e.Content is not null) == true) ? 1 : 0;
                withDocumentPages += layers.Any(static l => l.ErrorPages?.Entries().Any(static e => e.Content is { Mode: ErrorPageMode.Html }) == true) ? 1 : 0;
                withMaintenance += layers.Any(static l => l.Maintenance?.Enabled == true) ? 1 : 0;
                withBypass += layers.Any(static l => l.Maintenance?.BypassAccessListId is not null) ? 1 : 0;
                withReplacement += layers.Any(static l => l.ErrorPages?.ReplaceUpstream is { } mode && mode != UpstreamErrorReplacement.Off) ? 1 : 0;
                var compilation = _certificates.Compile(document, Revision);
                Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
                Assert.NotNull(compilation.Snapshot);
            });

        // The generator has to reach the compiler, or the property says nothing.
        Assert.True(valid * 8 >= total, $"only {valid} of {total} generated documents validate");

        // Nor does it say much if the M8 settings never reach it, in both their valid and their refused forms.
        Assert.True(withPages >= 30 && withDocumentPages >= 10 && withMaintenance >= 30 && withBypass >= 5 && withReplacement >= 10, $"valid documents with pages {withPages}, with a document page {withDocumentPages}, in maintenance {withMaintenance}, with a bypass {withBypass}, replacing upstream errors {withReplacement}");
        Assert.True(refusedPages >= 10 && refusedMaintenance >= 3, $"documents refused for their pages {refusedPages} and for their maintenance {refusedMaintenance}");
    }

    private static IEnumerable<HostSettings> Layers(ConfigDocument document) =>
        [document.Settings.Defaults, .. document.Profiles.Select(static p => p.Settings), .. document.Hosts.Select(static h => h.Settings)];

    [Fact]
    public void CanonicalJsonIsIdempotentRoundTripsAndKeepsAValidDocumentValid()
    {
        var valid = 0;
        ConfigGen.Documents.Check(
            500,
            document =>
            {
                var bytes = CanonicalConfigJson.Serialize(document);
                Assert.True(CanonicalConfigJson.TryDeserialize(bytes, out var read, out var error), error?.Message);
                Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(read)));
                Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(CanonicalConfigJson.Canonicalize(document))));
                Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(CanonicalConfigJson.Canonicalize(read))));
                Assert.Empty(ConfigDiff.Compare(CanonicalConfigJson.Canonicalize(document), read));

                if (ConfigValidator.Validate(document).Count > 0)
                {
                    return;
                }

                // What is stored is read back and checked again at startup: a canonical document that stopped validating would be a stored revision the proxy can't load.
                valid++;
                var errors = ConfigValidator.Validate(read);
                Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
                var compilation = _certificates.Compile(read, Revision);
                Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
            });

        Assert.True(valid > 30, $"only {valid} generated documents validate");
    }

    [Fact]
    public void TheSameConfigWrittenDifferentlyHasTheSameCanonicalBytes()
    {
        var checkedDocuments = 0;
        Gen.Select(ConfigGen.Documents, Gen.Int[0, 1_000_000]).Check(
            400,
            t =>
            {
                var (document, seed) = t;
                if (ConfigValidator.Validate(document).Count > 0)
                {
                    return;
                }

                checkedDocuments++;
                var respelled = Respelled(document, seed);
                Assert.Equal(Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(respelled)));
            });

        Assert.True(checkedDocuments > 20, $"only {checkedDocuments} generated documents validate");
    }
}
