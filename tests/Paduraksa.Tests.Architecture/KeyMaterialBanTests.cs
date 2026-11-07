using System.Collections.Immutable;
using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// Guards <c>BannedSymbols.KeyMaterial.txt</c> (PLAN §8: Vault is the only key-material boundary).
/// BannedApiAnalyzers matches <c>M:</c> entries by exact doc-ID, and an entry without a parameter list matches only
/// the parameterless overload, so every overload must be listed (no wildcard form exists). These tests:
/// <list type="number">
/// <item>compute, with Roslyn, the doc-ID of every overload of each banned API family and require all of them in the
/// list (new overloads in a future .NET fail here instead of slipping through);</item>
/// <item>require every entry to resolve to a real API (no typos or stale signatures);</item>
/// <item>run the real analyzer on a probe that calls the APIs, so a format or analyzer change can't make the list
/// silently inert again.</item>
/// </list>
/// </summary>
public sealed class KeyMaterialBanTests
{
    private const string Crypto = "System.Security.Cryptography.";
    private const string X509 = "System.Security.Cryptography.X509Certificates.";

    private static readonly string[] AsymmetricTypes =
    [
        "AsymmetricAlgorithm", "RSA", "DSA", "ECAlgorithm", "ECDsa", "ECDiffieHellman",
        "MLDsa", "MLKem", "SlhDsa", "CompositeMLDsa",
    ];

    /// <summary>
    /// Types that are banned whole (any use, including the static <c>HashData</c>, <c>TryHashData</c> and
    /// <c>HashDataAsync</c> overloads, which a type ban covers). All keyed hashes: a MAC with a key in hand belongs to the vault.
    /// </summary>
    private static readonly string[] FullyBannedTypes =
    [
        Crypto + "AesGcm",
        Crypto + "HMAC", Crypto + "KeyedHashAlgorithm",
        Crypto + "HMACMD5", Crypto + "HMACSHA1", Crypto + "HMACSHA256", Crypto + "HMACSHA384", Crypto + "HMACSHA512",
        Crypto + "HMACSHA3_256", Crypto + "HMACSHA3_384", Crypto + "HMACSHA3_512",
        Crypto + "Kmac128", Crypto + "Kmac256", Crypto + "KmacXof128", Crypto + "KmacXof256",
    ];

    private static readonly Lazy<CSharpCompilation> Framework = new(CreateCompilation);

    /// <summary>(type, member predicate): every public overload matching must be banned.</summary>
    private static IEnumerable<(string Type, Func<ISymbol, bool> Member)> Rules()
    {
        yield return (X509 + "X509Certificate", static m => m.Name.StartsWith("Export", StringComparison.Ordinal));

        // PFX export of a collection carries the keys of its members; the PEM export is certificates only.
        yield return (X509 + "X509Certificate2Collection", static m => m.Name is "Export" or "ExportPkcs12");

        // Only the PKCS#12 loaders yield private keys. LoadCertificate* reads public certificates (it rejects a PFX
        // and ignores the key blocks of a PEM: HasPrivateKey is false), so Acme can parse chains it receives.
        yield return (X509 + "X509CertificateLoader", static m => m.Name.StartsWith("LoadPkcs12", StringComparison.Ordinal));
        yield return (Crypto + "IncrementalHash", static m => m.Name == "CreateHMAC");
        yield return (X509 + "X509Certificate2", static m =>
            m.Name is "ExportPkcs12" or "CopyWithPrivateKey" or "PrivateKey"
                or "CreateFromPem" or "CreateFromPemFile" or "CreateFromEncryptedPem" or "CreateFromEncryptedPemFile"
            || (m.Name.StartsWith("Get", StringComparison.Ordinal) && m.Name.EndsWith("PrivateKey", StringComparison.Ordinal)));
        foreach (var extensions in new[] { "RSACertificateExtensions", "ECDsaCertificateExtensions", "DSACertificateExtensions" })
        {
            yield return (X509 + extensions, static m =>
                m.Name == "CopyWithPrivateKey"
                || (m.Name.StartsWith("Get", StringComparison.Ordinal) && m.Name.EndsWith("PrivateKey", StringComparison.Ordinal)));
        }

        foreach (var type in AsymmetricTypes)
        {
            yield return (Crypto + type, static m => IsPrivateKeyTransfer(m.Name));
        }
    }

    /// <summary>Exports and imports of private key material (public-key and SPKI forms stay allowed).</summary>
    private static bool IsPrivateKeyTransfer(string name)
    {
        var transfer = name.StartsWith("Export", StringComparison.Ordinal)
            || name.StartsWith("TryExport", StringComparison.Ordinal)
            || name.StartsWith("Import", StringComparison.Ordinal);
        if (!transfer)
        {
            return name is "ToXmlString" or "FromXmlString";
        }

        return name.Contains("Private", StringComparison.Ordinal)
            || name.Contains("Pkcs8", StringComparison.Ordinal)
            || name.Contains("Parameters", StringComparison.Ordinal)
            || name.Contains("Pem", StringComparison.Ordinal) && name.StartsWith("Import", StringComparison.Ordinal)
            || name.Contains("Seed", StringComparison.Ordinal)
            || name.Contains("Decapsulation", StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOverloadOfEveryKeyMaterialApiIsBanned()
    {
        var banned = BannedIds();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in FullyBannedTypes)
        {
            Require(type);
            if (!banned.Contains("T:" + type))
            {
                missing.Add("T:" + type);
            }
        }

        foreach (var (typeName, member) in Rules())
        {
            var type = Require(typeName);
            var matched = 0;
            foreach (var symbol in type.GetMembers())
            {
                if (symbol.DeclaredAccessibility != Accessibility.Public || symbol is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol) || !member(symbol))
                {
                    continue;
                }

                matched++;
                var id = symbol.GetDocumentationCommentId()!;
                if (!banned.Contains(id))
                {
                    missing.Add(id);
                }
            }

            Assert.True(matched > 0, $"Rule for {typeName} matches nothing: the API family moved or was renamed.");
        }

        Assert.True(
            missing.Count == 0,
            $"Add these to BannedSymbols.KeyMaterial.txt (one line per overload, ';' + reason):{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [Fact]
    public void EveryBanResolvesToAnApi()
    {
        var unresolved = new List<string>();
        foreach (var id in BannedIds())
        {
            if (DocumentationCommentId.GetSymbolsForDeclarationId(id, Framework.Value).IsEmpty)
            {
                unresolved.Add(id);
            }
        }

        Assert.True(unresolved.Count == 0, $"Entries that match no API (typo or stale signature): {string.Join(", ", unresolved)}");
    }

    [Fact]
    public async Task AnalyzerFlagsKeyMaterialCallsAndNothingElse()
    {
        const string Probe = """
            using System.Net.Security;
            using System.Security.Cryptography;
            using System.Security.Cryptography.X509Certificates;

            static class Probe
            {
                static void Use(X509Certificate2 c, X509Certificate2Collection coll, RSA rsa, ECDsa ec, DSA dsa, ECDiffieHellman dh, RSAOpenSsl openSsl, string pem, byte[] bytes)
                {
                    c.Export(X509ContentType.Pfx); // BAN
                    c.Export(X509ContentType.Pkcs12, "pw"); // BAN
                    c.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "pw"); // BAN
                    c.CopyWithPrivateKey(ec); // BAN
                    c.CopyWithPrivateKey(rsa); // BAN
                    c.CopyWithPrivateKey(dsa); // BAN
                    c.CopyWithPrivateKey(dh); // BAN
                    c.GetRSAPrivateKey(); // BAN
                    c.GetECDsaPrivateKey(); // BAN
                    c.GetDSAPrivateKey(); // BAN
                    c.GetECDiffieHellmanPrivateKey(); // BAN
                    _ = c.PrivateKey; // BAN
                    X509Certificate2.CreateFromPem(pem, pem); // BAN
                    X509Certificate2.CreateFromPemFile(pem); // BAN
                    X509Certificate2.CreateFromEncryptedPem(pem, pem, pem); // BAN
                    X509CertificateLoader.LoadPkcs12(bytes, "pw"); // BAN
                    X509CertificateLoader.LoadPkcs12FromFile(pem, "pw"); // BAN
                    X509CertificateLoader.LoadPkcs12Collection(bytes, "pw"); // BAN
                    X509CertificateLoader.LoadPkcs12CollectionFromFile(pem, "pw"); // BAN
                    coll.Export(X509ContentType.Pfx); // BAN
                    coll.Export(X509ContentType.Pkcs12, "pw"); // BAN
                    coll.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "pw"); // BAN
                    rsa.ExportParameters(true); // BAN
                    openSsl.ExportParameters(true); // BAN
                    ec.ExportParameters(true); // BAN
                    rsa.ExportRSAPrivateKey(); // BAN
                    rsa.ExportRSAPrivateKeyPem(); // BAN
                    ec.ExportECPrivateKey(); // BAN
                    rsa.ExportPkcs8PrivateKey(); // BAN
                    rsa.ExportPkcs8PrivateKeyPem(); // BAN
                    rsa.TryExportPkcs8PrivateKey(bytes, out _); // BAN
                    rsa.ImportPkcs8PrivateKey(bytes, out _); // BAN
                    rsa.ImportFromPem(pem); // BAN
                    ec.ImportFromPem(pem); // BAN
                    new AesGcm(bytes, 16).Dispose(); // BAN
                    HMACSHA256.HashData(bytes, bytes); // BAN
                    HMACSHA512.HashData(bytes, bytes); // BAN
                    HMACSHA384.HashData(bytes, bytes); // BAN
                    HMACSHA1.HashData(bytes, bytes); // BAN
                    HMACMD5.TryHashData(bytes, bytes, bytes, out _); // BAN
                    new HMACSHA3_256(bytes).Dispose(); // BAN
                    IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, bytes).Dispose(); // BAN

                    X509CertificateLoader.LoadCertificate(bytes).Dispose();
                    X509CertificateLoader.LoadCertificateFromFile(pem).Dispose();
                    coll.ExportCertificatePems();
                    IncrementalHash.CreateHash(HashAlgorithmName.SHA256).Dispose();
                    SHA256.HashData(bytes);
                    c.GetPublicKey();
                    c.ExportCertificatePem();
                    rsa.ExportSubjectPublicKeyInfo();
                    rsa.ImportSubjectPublicKeyInfo(bytes, out _);
                    SslStreamCertificateContext.Create(c, null, offline: true);
                }
            }
            """;

        var compilation = Framework.Value.AddSyntaxTrees(CSharpSyntaxTree.ParseText(Probe, path: "Probe.cs", cancellationToken: TestContext.Current.CancellationToken));
        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, $"Probe doesn't compile: {string.Join(Environment.NewLine, errors.Select(static e => e.ToString()))}");

        var withAnalyzers = compilation.WithAnalyzers(
            LoadBannedApiAnalyzers(),
            new AnalyzerOptions([new FileText(BanFilePath)]));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        var flagged = diagnostics.Where(static d => d.Id == "RS0030").Select(static d => d.Location.GetLineSpan().StartLinePosition.Line + 1).ToHashSet();
        var lines = Probe.Split('\n');
        var expected = Enumerable.Range(1, lines.Length).Where(n => lines[n - 1].TrimEnd().EndsWith("// BAN", StringComparison.Ordinal)).ToHashSet();

        var notFlagged = expected.Except(flagged).Select(n => lines[n - 1].Trim()).ToArray();
        var wronglyFlagged = flagged.Except(expected).Select(n => lines[n - 1].Trim()).ToArray();
        Assert.True(notFlagged.Length == 0, $"Not flagged by RS0030:{Environment.NewLine}{string.Join(Environment.NewLine, notFlagged)}");
        Assert.True(wronglyFlagged.Length == 0, $"Flagged but allowed:{Environment.NewLine}{string.Join(Environment.NewLine, wronglyFlagged)}");
    }

    private static INamedTypeSymbol Require(string metadataName) =>
        Framework.Value.GetTypeByMetadataName(metadataName) ?? throw new InvalidOperationException($"{metadataName} not found in the shared framework.");

    private static HashSet<string> BannedIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(BanFilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var semicolon = line.IndexOf(';', StringComparison.Ordinal);
            Assert.True(semicolon > 0, $"Every ban needs ';' and a reason: {line}");
            Assert.True(ids.Add(line[..semicolon]), $"Duplicate ban: {line}");
        }

        return ids;
    }

    private static CSharpCompilation CreateCompilation()
    {
        // The shared framework (implementation assemblies): same doc-IDs as the reference assemblies the build uses.
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = Directory.GetFiles(frameworkDirectory, "*.dll")
            .Where(static path => IsManagedAssembly(path))
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "KeyMaterialProbe",
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static ImmutableArray<DiagnosticAnalyzer> LoadBannedApiAnalyzers()
    {
        var directory = typeof(KeyMaterialBanTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(static a => a.Key == "BannedApiAnalyzersDirectory").Value!;
        var loader = new LoadFromLoader();
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        foreach (var file in new[] { "Microsoft.CodeAnalysis.BannedApiAnalyzers.dll", "Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers.dll" })
        {
            analyzers.AddRange(new AnalyzerFileReference(Path.Combine(directory, file), loader).GetAnalyzers(LanguageNames.CSharp));
        }

        Assert.NotEmpty(analyzers);
        return analyzers.ToImmutable();
    }

    private static string BanFilePath { get; } = Path.Combine(FindRepoRoot(), "BannedSymbols.KeyMaterial.txt");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Paduraksa.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Paduraksa.slnx not found above {AppContext.BaseDirectory}");
    }

    private sealed class FileText(string path) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(Path));
    }

    private sealed class LoadFromLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}
