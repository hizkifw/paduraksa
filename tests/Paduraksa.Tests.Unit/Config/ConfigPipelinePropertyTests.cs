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
        ConfigGen.Documents.Check(
            1_500,
            document =>
            {
                total++;
                var errors = ConfigValidator.Validate(document);
                Assert.Equal(errors.Count, ConfigValidator.Validate(document).Count);
                if (errors.Count > 0)
                {
                    return;
                }

                valid++;
                var compilation = _certificates.Compile(document, Revision);
                Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
                Assert.NotNull(compilation.Snapshot);
            });

        // The generator has to reach the compiler, or the property says nothing.
        Assert.True(valid * 8 >= total, $"only {valid} of {total} generated documents validate");
    }

    [Fact]
    public void CanonicalJsonIsIdempotentRoundTripsAndKeepsAValidDocumentValid()
    {
        var valid = 0;
        ConfigGen.Documents.Check(
            1_500,
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

        Assert.True(valid > 50, $"only {valid} generated documents validate");
    }

    [Fact]
    public void TheSameConfigWrittenDifferentlyHasTheSameCanonicalBytes()
    {
        var checkedDocuments = 0;
        Gen.Select(ConfigGen.Documents, Gen.Int[0, 1_000_000]).Check(
            800,
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

        Assert.True(checkedDocuments > 30, $"only {checkedDocuments} generated documents validate");
    }
}
