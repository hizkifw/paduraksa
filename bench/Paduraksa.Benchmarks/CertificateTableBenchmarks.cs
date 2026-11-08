using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Benchmarks;

/// <summary>
/// <see cref="CertificateTable.Find(ReadOnlySpan{char})"/> (per-SNI selection on the TLS handshake) over ~500 names: exact, case-folded
/// with a trailing dot, single-label wildcard, the Unicode form of an IDN name (what <c>SslStream</c> hands the TCP
/// callback), and an unknown name falling back to the default. All names share one throwaway certificate.
/// </summary>
[MemoryDiagnoser]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet disposes it in [GlobalCleanup].")]
public class CertificateTableBenchmarks
{
    private X509Certificate2? _leaf;
    private CertificateTable _table = CertificateTable.Empty;

    [Params("h123.test", "H123.TEST.", "a.w7.test", "bücher.test", "nope.example.org")]
    public string ServerName { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _leaf = CreateLeaf();
        ServerCertificate certificate = ServerCertificate.Create(_leaf, intermediates: null);
        CertificateTableBuilder builder = new();
        for (int i = 0; i < BenchConfig.ExactHosts; i++)
        {
            builder.Add([$"h{i}.test"], certificate, TlsPolicy.Default);
        }

        for (int i = 0; i < BenchConfig.WildcardHosts; i++)
        {
            builder.Add([$"*.w{i}.test"], certificate, TlsPolicy.Default);
        }

        builder.Add(["xn--bcher-kva.test"], certificate, TlsPolicy.Default);
        builder.SetDefault(certificate, TlsPolicy.Default);
        _table = builder.Build();
        if (_table.Find(ServerName) is null)
        {
            throw new InvalidOperationException($"No entry for '{ServerName}'.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _leaf?.Dispose();

    [Benchmark]
    public CertificateTableEntry? Find() => _table.Find(ServerName);

    private static X509Certificate2 CreateLeaf()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=Paduraksa benchmark", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
    }
}
