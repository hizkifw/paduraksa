using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Data.Config;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>
/// A <see cref="ConfigService"/> over a test database with the test defaults spelled out in one place: the stand-in compiler,
/// every permission held, an in-memory password registry, the system clock, no logging, no certificates in the vault, and a last-known-good file next to the
/// database (disposed with it). Production passes all of these; a test names only what it changes.
/// </summary>
internal static class TestConfigServices
{
    public static ConfigService Create(
        TestConfigDatabase db,
        SnapshotPublisher publisher,
        IConfigCompiler? compiler = null,
        IPermissionEvaluator? permissions = null,
        LastKnownGoodFile? lastKnownGood = null,
        TimeProvider? time = null,
        ILoggerFactory? loggers = null,
        ConfigServiceOptions? options = null,
        IServerCertificateSource? certificateSource = null,
        IBuiltInCertificates? builtIns = null,
        ICertificateCatalog? catalog = null,
        IPasswordCredentials? passwords = null) =>
        new(
            db.Factory,
            compiler ?? new ConfigServiceHarness.FakeCompiler(),
            publisher,
            permissions ?? TestPermissions.AllowAll,
            time ?? TimeProvider.System,
            loggers ?? NullLoggerFactory.Instance,
            options ?? new ConfigServiceOptions(),
            lastKnownGood ?? db.Own(new LastKnownGoodFile(Path.Combine(Path.GetDirectoryName(db.DatabasePath)!, "unshared-" + LastKnownGoodFile.FileName), NullLoggerFactory.Instance)),
            certificateSource ?? new NoCertificates(),
            builtIns,
            catalog ?? new NoCertificates(),
            passwords ?? new TestPasswords());

    /// <summary>A vault that holds no certificate.</summary>
    internal sealed class NoCertificates : IServerCertificateSource, ICertificateCatalog
    {
        public ValueTask<System.Net.Security.SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<System.Net.Security.SslStreamCertificateContext?>(null);

        public ValueTask<IReadOnlyList<VaultCertificate>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<VaultCertificate>>([]);

        public ValueTask<VaultCertificate?> DescribeAsync(CertificateId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<VaultCertificate?>(null);
    }
}
