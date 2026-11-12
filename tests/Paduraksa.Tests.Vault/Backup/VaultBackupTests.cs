using System.Buffers.Text;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault;
using Paduraksa.Vault.Backup;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Tests.Vault.Backup;

public sealed class VaultBackupTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private static readonly byte[] MacKey = RandomNumberGenerator.GetBytes(32);

    private readonly VaultHarness _source = new();

    private readonly TempDirectory _config = new();

    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _disposables.ForEach(d => d.Dispose());
        SqliteConnection.ClearAllPools();
        _source.Dispose();
        _config.Dispose();
    }

    /// <summary>A stand-in for the config database: the vault archives it as an opaque SQLite file with an EF history.</summary>
    private string ConfigDatabase(params string[] migrations)
    {
        var path = _config.File("paduraksa.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT); CREATE TABLE IF NOT EXISTS Notes (Id INTEGER PRIMARY KEY, Body TEXT);";
            command.ExecuteNonQuery();
            foreach (var migration in migrations)
            {
                command.CommandText = "INSERT OR IGNORE INTO __EFMigrationsHistory VALUES ($id, '10.0.0')";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", migration);
                command.ExecuteNonQuery();
            }

            command.CommandText = "INSERT INTO Notes (Body) VALUES ('hello from the source')";
            command.ExecuteNonQuery();
        }

        return path;
    }

    private BackupSources Sources(string configPath)
    {
        var lastGood = _config.File("last-good.json");
        File.WriteAllText(lastGood, """{"format":1,"revision":7}""");
        return new BackupSources(configPath, lastGood);
    }

    private async Task<byte[]> BackupAsync(BackupSources sources, string passphrase = Passphrase)
    {
        using MemoryStream stream = new();
        await _source.Get<VaultBackup>().CreateAsync(sources, passphrase.AsMemory(), stream, memoryKiB: 64, iterations: 1, parallelism: 1, Ct);
        return stream.ToArray();
    }

    /// <summary>A second machine: another data directory and another master key.</summary>
    private VaultHarness Target()
    {
        var target = new VaultHarness();
        _disposables.Add(target);
        return target;
    }

    private static async Task<IStagedBackup> OpenAsync(VaultHarness target, byte[] archive, string passphrase = Passphrase, string[]? knownConfig = null)
    {
        using MemoryStream stream = new(archive);
        return await target.Get<VaultBackup>().OpenAsync(stream, passphrase.AsMemory(), Path.Combine(target.Data.Path, "run", "restore-test"), knownConfig ?? ["20260101_Initial"], Ct);
    }

    private static BackupFailure FailureOf(Task task) => Assert.ThrowsAsync<BackupException>(() => task).GetAwaiter().GetResult().Reason;

    [Fact]
    public async Task EveryKindOfSecretRestoresOnAnotherMachineUnderItsOwnMasterKey()
    {
        // The source holds one of everything that is sealed.
        var secrets = _source.Get<ISecretStore>();
        var dns = await secrets.PutAsync(SecretKind.DnsApiToken, "dns-token"u8.ToArray(), new("dns", null, ["api.example.com"]), Ct);
        var mac = await secrets.PutAsync(SecretKind.HmacSigningKey, MacKey, new(null, MacAlgorithm.HmacSha256, null), Ct);
        var newerMac = RandomNumberGenerator.GetBytes(32);
        await secrets.ReplaceAsync(mac, newerMac, null, Ct);
        var account = AcmeAccountId.New();
        var accountKey = await _source.Get<IJwsSigner>().CreateAccountKeyAsync(account, JwsAlgorithm.ES256, Ct);
        var certificate = CertificateId.New();
        var certificateInfo = await _source.Get<ICertificateIssuance>().CreateSelfSignedAsync(certificate, KeySpec.EcdsaP256, ["app.example.com"], TimeSpan.FromDays(30), Ct);
        var authority = CertificateId.New();
        await _source.Get<ICertificateIssuance>().EnsureLocalCaAsync(authority, KeySpec.EcdsaP256, TimeSpan.FromDays(365), Ct);
        await _source.Get<IDataProtectionKeyStore>().StoreAsync("key-1", "<key id=\"a\"><secret>s3cret</secret></key>", Ct);
        var password = PasswordCredentialId.New();
        await _source.Get<IPasswordCredentials>().SetAsync(password, "correct horse".AsMemory(), Ct);
        var totp = await _source.Get<ITotpAuthenticator>().BeginEnrollmentAsync("alice", "Paduraksa", Ct);
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));

        var target = Target();
        await using var staged = await OpenAsync(target, archive);
        var vaultPath = target.Data.File("vault.db");
        await staged.WriteVaultDatabaseAsync(vaultPath, Ct);

        // Another master key, which the restore has not been given: the target generates its own, as a first start does.
        Assert.Equal(KekSourceKind.Environment, (await target.Get<IVaultLifecycle>().InitializeAsync(Ct)).KekSource);
        await using (var context = await target.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            var targetKek = Convert.FromBase64String(target.Variables[KekProvider.KeyVariable]!);
            using var kek = Kek.FromBytes(targetKek);
            foreach (var sealedValue in (await context.Secrets.AsNoTracking().Select(s => s.Sealed).ToListAsync(Ct))
                .Concat(await context.AccountKeys.AsNoTracking().Select(k => k.Sealed).ToListAsync(Ct))
                .Concat(await context.CertificateKeys.AsNoTracking().Select(k => k.Sealed).ToListAsync(Ct))
                .Concat(await context.DataProtectionKeys.AsNoTracking().Select(k => k.Sealed).ToListAsync(Ct))
                .Concat(await context.PasswordCredentials.AsNoTracking().Select(k => k.Sealed).ToListAsync(Ct)))
            {
                Assert.Equal(kek.Id, EnvelopeCipher.KekIdOf(sealedValue).ToArray());
            }
        }

        // Every secret reads back what it held.
        using (var dnsValue = await target.Get<SecretReader>().OpenAsync(dns, includePrevious: false, Ct))
        {
            Assert.Equal("dns-token", Encoding.UTF8.GetString(dnsValue!.Current.Span));
        }

        using (var macValue = await target.Get<SecretReader>().OpenAsync(mac, includePrevious: true, Ct))
        {
            Assert.Equal(newerMac, macValue!.Current.Span.ToArray());
            Assert.Equal(MacKey, macValue.Previous!.Span.ToArray());
        }

        var signed = await target.Get<IJwsSigner>().SignAsync(account, Encoding.UTF8.GetBytes("""{"alg":"ES256","nonce":"n","url":"https://ca.example/","kid":"k"}"""), "{}"u8.ToArray(), Ct);
        using (var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Base64Url.DecodeFromChars(accountKey.Jwk.X), Y = Base64Url.DecodeFromChars(accountKey.Jwk.Y) } }))
        {
            Assert.True(ecdsa.VerifyData(Encoding.ASCII.GetBytes($"{signed.Protected}.{signed.Payload}"), Base64Url.DecodeFromChars(signed.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        var served = await target.Get<IServerCertificateSource>().LoadForServingAsync(certificate, Ct);
        Assert.Equal(certificateInfo.Sha256Fingerprint, Convert.ToHexStringLower(SHA256.HashData(served!.TargetCertificate.RawData)));
        Assert.NotNull(served.TargetCertificate.GetECDsaPrivateKey());
        Assert.Contains(authority, (await target.Get<ICertificateCatalog>().ListAsync(Ct)).Select(c => c.Id));

        var keys = await target.Get<IDataProtectionKeyStore>().ReadAllAsync(Ct);
        Assert.Equal("<key id=\"a\"><secret>s3cret</secret></key>", Assert.Single(keys).KeyXml);

        var handle = target.Get<IPasswordCredentials>().Open(password);
        Assert.Equal(PasswordVerdict.Verified, target.Get<IPasswordVerifier>().Verify(handle, "alice"u8, "correct horse"u8, new byte[16], remember: false));

        Assert.Equal(SecretKind.TotpSecret, (await target.Get<ISecretStore>().DescribeAsync(totp.SecretId, Ct))!.Kind);
    }

    [Fact]
    public async Task TheArchiveNeverHoldsTheSourceMasterKeyAndItsVaultIsNotUnderIt()
    {
        await _source.Get<ISecretStore>().PutAsync(SecretKind.DnsApiToken, "dns-token"u8.ToArray(), new("dns", null, ["api.example.com"]), Ct);
        var sourceKey = _source.Variables[KekProvider.KeyVariable]!;
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));

        Assert.DoesNotContain(Encoding.ASCII.GetBytes(sourceKey), archive);
        var target = Target();
        await using var staged = await OpenAsync(target, archive);
        var scratch = target.Data.File("scratch-vault.db");
        await staged.WriteVaultDatabaseAsync(scratch, Ct);

        // The restored file is under the target's key, and so no wrapped key in it carries the source's identifier.
        using var sourceKek = Kek.FromBytes(Convert.FromBase64String(sourceKey));
        await using var context = new VaultDbContext(VaultBackup.ScratchOptions(scratch));
        Assert.All(await context.Secrets.AsNoTracking().Select(s => s.Sealed).ToListAsync(Ct), v => Assert.NotEqual(sourceKek.Id, EnvelopeCipher.KekIdOf(v).ToArray()));
        Assert.False((await context.Meta.AsNoTracking().SingleAsync(Ct)).KekCheck.AsSpan().SequenceEqual(sourceKek.CheckValue));
    }

    [Fact]
    public async Task AFreshTargetWithNoMasterKeyGetsOneGeneratedAsAtAFirstStart()
    {
        var restoredId = await _source.Get<ISecretStore>().PutAsync(SecretKind.DnsApiToken, "dns-token"u8.ToArray(), new("dns", null, ["api.example.com"]), Ct);
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();
        target.Variables.Clear();
        await using var staged = await OpenAsync(target, archive);

        await staged.WriteVaultDatabaseAsync(target.Data.File("vault.db"), Ct);

        Assert.True(File.Exists(target.Data.File("master.key")));
        var status = await target.Get<IVaultLifecycle>().InitializeAsync(Ct);
        Assert.Equal(KekSourceKind.GeneratedFile, status.KekSource);
        Assert.NotNull(await target.Get<ISecretStore>().DescribeAsync(restoredId, Ct));
    }

    [Fact]
    public async Task TheStagedConfigDatabaseAndLastKnownGoodAreTheSourcesBytes()
    {
        var sources = Sources(ConfigDatabase("20260101_Initial"));
        var archive = await BackupAsync(sources);

        var target = Target();
        await using var staged = await OpenAsync(target, archive);

        Assert.Equal(["20260101_Initial"], staged.Manifest.ConfigMigrations);
        Assert.Equal(new BackupKdf("argon2id", 19, 64, 1, 1), staged.Manifest.Kdf);
        Assert.Equal("""{"format":1,"revision":7}""", File.ReadAllText(staged.LastKnownGoodPath!));
        using var connection = new SqliteConnection($"Data Source={staged.ConfigDatabasePath};Pooling=false;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Body FROM Notes";
        Assert.Equal("hello from the source", command.ExecuteScalar());
    }

    [Fact]
    public async Task ABackupWithoutALastKnownGoodFileLeavesItOut()
    {
        var archive = await BackupAsync(new BackupSources(ConfigDatabase("20260101_Initial"), _config.File("missing.json")));

        var target = Target();
        await using var staged = await OpenAsync(target, archive);

        Assert.Null(staged.LastKnownGoodPath);
        Assert.DoesNotContain(staged.Manifest.Entries, e => e.Name == "last-good.json");
    }

    [Fact]
    public async Task AWrongPassphraseIsAWrongPassphraseAndLeavesNoStagingDirectory()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();

        Assert.Equal(BackupFailure.WrongPassphrase, FailureOf(OpenAsync(target, archive, "not the passphrase")));
        Assert.False(Directory.Exists(Path.Combine(target.Data.Path, "run", "restore-test")));
    }

    [Fact]
    public async Task ATamperedByteAnywhereIsRefusedBeforeAnythingIsWritten()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();

        // Sampled across the file: the header, the first block, the middle, the last block and its tag.
        foreach (var position in new[] { 3, 9, 14, 30, 50, 100, archive.Length / 2, archive.Length - 20, archive.Length - 1 })
        {
            var copy = archive.ToArray();
            copy[position] ^= 0x10;
            var failure = FailureOf(OpenAsync(target, copy));
            Assert.True(failure is BackupFailure.NotABackup or BackupFailure.UnsupportedFormat or BackupFailure.WrongPassphrase or BackupFailure.Damaged, $"byte {position}: {failure}");
            Assert.False(Directory.Exists(Path.Combine(target.Data.Path, "run", "restore-test")), $"byte {position} left a staging directory");
        }

        Assert.False(File.Exists(target.Data.File("vault.db")));
    }

    [Fact]
    public async Task ATruncatedFileIsRefused()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();

        foreach (var length in new[] { 0, 10, BackupHeader.Length, BackupHeader.Length + 5, archive.Length / 2, archive.Length - 1 })
        {
            Assert.True(FailureOf(OpenAsync(target, archive[..length])) is BackupFailure.NotABackup or BackupFailure.WrongPassphrase or BackupFailure.Damaged, $"length {length}");
        }
    }

    [Fact]
    public async Task AFileThatIsNotABackupIsSaidSo()
    {
        var target = Target();

        Assert.Equal(BackupFailure.NotABackup, FailureOf(OpenAsync(target, Encoding.ASCII.GetBytes("This is a text file, longer than a backup header is, and it is not a backup."))));
    }

    [Fact]
    public async Task ADatabaseFromANewerSchemaIsRefusedNamingTheMigration()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial", "29990101_FromTheFuture")));
        var target = Target();

        var ex = await Assert.ThrowsAsync<BackupException>(() => OpenAsync(target, archive, knownConfig: ["20260101_Initial"]));

        Assert.Equal(BackupFailure.SchemaTooNew, ex.Reason);
        Assert.Contains("29990101_FromTheFuture", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(target.Data.Path, "run", "restore-test")));
    }

    [Fact]
    public async Task AnOlderConfigSchemaIsAcceptedAndLeftForTheNextStartToMigrate()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();

        await using var staged = await OpenAsync(target, archive, knownConfig: ["20260101_Initial", "20260202_Later"]);

        Assert.Equal(["20260101_Initial"], staged.Manifest.ConfigMigrations);
    }

    [Fact]
    public async Task ADisposedStagedBackupRemovesItsDirectory()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();
        var staged = await OpenAsync(target, archive);
        var directory = Path.GetDirectoryName(staged.ConfigDatabasePath)!;
        Assert.True(Directory.Exists(directory));

        await staged.DisposeAsync();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task TheStagingDirectoryAndItsFilesAreOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes.");
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();
        await using var staged = await OpenAsync(target, archive);

#pragma warning disable CA1416 // Skipped on Windows above.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(staged.ConfigDatabasePath)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(staged.ConfigDatabasePath));
#pragma warning restore CA1416
    }

    [Fact]
    public async Task ARestoredVaultFileIsOwnerOnlyFromTheStart()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes.");
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();
        await using var staged = await OpenAsync(target, archive);

        await staged.WriteVaultDatabaseAsync(target.Data.File("vault.db"), Ct);

#pragma warning disable CA1416 // Skipped on Windows above.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target.Data.File("vault.db")));
#pragma warning restore CA1416
    }

    [Fact]
    public async Task ARestoreRefusesToWriteOverAnExistingVaultDatabase()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var target = Target();
        await target.Get<IVaultLifecycle>().InitializeAsync(Ct);
        await using var staged = await OpenAsync(target, archive);

        await Assert.ThrowsAsync<InvalidOperationException>(() => staged.WriteVaultDatabaseAsync(target.Data.File("vault.db"), Ct).AsTask());
    }

    [Fact]
    public async Task TheScratchFilesOfABackupAreGoneAfterwards()
    {
        await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));

        Assert.Empty(Directory.GetDirectories(Path.Combine(_source.Data.Path, "run")));
    }

    [Fact]
    public async Task ASweepLeavesAWorkDirectoryWhoseMarkerIsHeldAndRemovesAbandonedOnes()
    {
        var run = Path.Combine(_source.Data.Path, "run");
        var running = Path.Combine(run, "backup-aaaaaaaaaaaaaaaa");
        var abandoned = Path.Combine(run, "backup-bbbbbbbbbbbbbbbb");
        var markerless = Path.Combine(run, "backup-cccccccccccccccc");
        var markerlessYoung = Path.Combine(run, "backup-dddddddddddddddd");
        foreach (var directory in new[] { running, abandoned, markerless, markerlessYoung })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "paduraksa.db"), "plain snapshot");
        }

        File.WriteAllText(Path.Combine(abandoned, ".lock"), string.Empty);
        // Another process's backup, mid-write: it holds its marker, however old the directory looks.
        using var held = new FileStream(Path.Combine(running, ".lock"), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None });
        var longAgo = VaultHarness.Epoch.UtcDateTime.AddHours(-5);
        foreach (var directory in new[] { running, abandoned, markerless })
        {
            Directory.SetLastWriteTimeUtc(directory, longAgo);
        }

        await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));

        Assert.True(File.Exists(Path.Combine(running, "paduraksa.db")));
        Assert.True(Directory.Exists(markerlessYoung));
        Assert.False(Directory.Exists(abandoned));
        Assert.False(Directory.Exists(markerless));
    }

    [Fact]
    public async Task AnEntryWhoseSizeDisagreesWithTheManifestIsRefusedBeforeItIsWritten()
    {
        var header = BackupHeader.Create(64, 1, 1, RandomNumberGenerator.GetBytes(BackupHeader.SaltLength));
        var key = BackupKeyDerivation.Derive(Passphrase, header.Salt, 64, 1, 1);
        byte[] content = [1, 2, 3, 4];
        string[] names = ["paduraksa.db", "vault.db", "vault-backup.key"];
        var manifest = new BackupManifest(
            1, "test", DateTimeOffset.UtcNow, [], [],
            [.. names.Select(n => new BackupEntry(n, n == "paduraksa.db" ? content.Length + 1 : content.Length, Convert.ToHexStringLower(SHA256.HashData(content))))],
            header.Kdf);
        using MemoryStream archive = new();
        using (var encrypt = new AeadEncryptStream(archive, key, header))
        {
            using (var tar = new TarWriter(encrypt, TarEntryFormat.Ustar, leaveOpen: true))
            {
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = new MemoryStream(BackupManifestFormat.Write(manifest)) });
                foreach (var name in names)
                {
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
                }
            }

            encrypt.Complete();
        }

        var target = Target();
        var ex = await Assert.ThrowsAsync<BackupException>(() => OpenAsync(target, archive.ToArray()));

        Assert.Equal(BackupFailure.Damaged, ex.Reason);
        Assert.Contains("paduraksa.db does not match its manifest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWeakPassphraseIsRefusedBeforeAnythingIsRead()
    {
        using MemoryStream stream = new();

        var ex = await Assert.ThrowsAsync<BackupException>(() => _source.Get<IVaultBackup>().CreateAsync(new BackupSources(_config.File("nope.db"), null), "short".AsMemory(), stream, Ct).AsTask());

        Assert.Equal(BackupFailure.WeakPassphrase, ex.Reason);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ASecondBackupWhileOneRunsIsBusyNotQueued()
    {
        var sources = Sources(ConfigDatabase("20260101_Initial"));
        using var slow = new SlowStream();
        var first = _source.Get<VaultBackup>().CreateAsync(sources, Passphrase.AsMemory(), slow, 64, 1, 1, Ct).AsTask();
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var ex = await Assert.ThrowsAsync<BackupException>(() => _source.Get<VaultBackup>().CreateAsync(sources, Passphrase.AsMemory(), new MemoryStream(), 64, 1, 1, Ct).AsTask());
        slow.Release.SetResult();
        await first;

        Assert.Equal(BackupFailure.Busy, ex.Reason);
    }

    [Fact]
    public async Task ABackupOfAVaultUnderAnotherKeyThanTheProcessHoldsFailsAndSaysWhy()
    {
        var sources = Sources(ConfigDatabase("20260101_Initial"));
        await _source.Get<IVaultLifecycle>().InitializeAsync(Ct);
        // A rotation from another process: the database is now under a key this process does not hold.
        using (var other = _source.Another())
        {
            using var newKek = Kek.Generate();
            await other.GetRequiredService<KekRotation>().RotateAsync(newKek, Ct);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BackupAsync(sources));

        Assert.Contains("another master key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackupTakenWhileTheServerWritesIsAConsistentSnapshot()
    {
        var sources = Sources(ConfigDatabase("20260101_Initial"));
        var secrets = _source.Get<ISecretStore>();
        List<SecretId> ids = [];
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await secrets.PutAsync(SecretKind.DnsApiToken, Encoding.UTF8.GetBytes("round--1"), new("dns", null, ["api.example.com"]), Ct));
        }

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(
            async () =>
            {
                // Each replacement is one transaction: a snapshot holds a value from before it or from after it, never a mix, and a torn
                // row would fail to open (its sealed value is bound to its id and version).
                for (var round = 0; !stop.IsCancellationRequested; round++)
                {
                    foreach (var id in ids)
                    {
                        await secrets.ReplaceAsync(id, Encoding.UTF8.GetBytes("round-" + round.ToString(CultureInfo.InvariantCulture)), null, CancellationToken.None);
                    }
                }
            },
            CancellationToken.None);

        List<byte[]> archives = [];
        for (var i = 0; i < 4; i++)
        {
            archives.Add(await BackupAsync(sources));
        }

        await stop.CancelAsync();
        await writer;

        foreach (var archive in archives)
        {
            var target = Target();
            await using var staged = await OpenAsync(target, archive);
            await staged.WriteVaultDatabaseAsync(target.Data.File("vault.db"), Ct);
            await target.Get<IVaultLifecycle>().InitializeAsync(Ct);
            foreach (var id in ids)
            {
                using var opened = await target.Get<SecretReader>().OpenAsync(id, includePrevious: false, Ct);
                Assert.StartsWith("round-", Encoding.UTF8.GetString(opened!.Current.Span), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TheArchiveIsATarOfTheFixedEntriesInsideTheEncryptedStreamAndNothingElse()
    {
        var archive = await BackupAsync(Sources(ConfigDatabase("20260101_Initial")));
        var header = BackupHeader.Parse(archive);
        var key = BackupKeyDerivation.Derive(Passphrase, header.Salt, header.MemoryKiB, header.Iterations, header.Parallelism);

        using MemoryStream source = new(archive, BackupHeader.Length, archive.Length - BackupHeader.Length);
        using var decrypt = new AeadDecryptStream(source, key, archive.AsSpan(0, BackupHeader.Length).ToArray());
        using var tar = new TarReader(decrypt);
        List<string> names = [];
        while (tar.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
        }

        Assert.Equal(["manifest.json", "paduraksa.db", "vault.db", "last-good.json", "vault-backup.key"], names);
        // Nothing of the plaintext is visible in the sealed file.
        Assert.DoesNotContain("SQLite format 3"u8.ToArray(), archive);
    }

    private sealed class SlowStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Started.TrySetResult();
            Release.Task.Wait();
            base.Write(buffer);
        }
    }
}
