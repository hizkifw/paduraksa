using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>Throwaway server certificates for loopback listeners that no client validates.</summary>
internal static class TestCertificates
{
    private const string Subject = "O=Paduraksa test";

    /// <summary>Whether a server presented one of these: what a test client checks instead of a chain.</summary>
    public static bool IsTestCertificate(X509Certificate? certificate) => certificate?.Subject.EndsWith(Subject, StringComparison.Ordinal) == true;

    public static X509Certificate2 SelfSigned(string name = "localhost") =>
        Shared.SelfSigned.Create($"CN={name}, {Subject}", [name], serverAuth: true);
}
