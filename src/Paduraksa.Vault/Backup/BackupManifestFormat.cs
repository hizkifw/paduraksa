using System.Globalization;
using System.Text.Json;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Backup;

/// <summary>The names of the files in an archive. Nothing else is accepted, and no name is ever used as a path.</summary>
internal static class BackupEntries
{
    public const string Manifest = "manifest.json";

    public const string Config = "paduraksa.db";

    public const string Vault = "vault.db";

    public const string LastKnownGood = "last-good.json";

    public const string VaultKey = "vault-backup.key";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) { Manifest, Config, Vault, LastKnownGood, VaultKey };

    /// <summary>The entries every archive has.</summary>
    public static readonly string[] Required = [Config, Vault, VaultKey];
}

/// <summary>
/// The manifest's JSON, written and read by hand: a format that outlives this build's serializer settings, with every field
/// checked on the way in. See docs/design/vault-and-certificates.md "Backup and restore (M10)", for the example.
/// </summary>
internal static class BackupManifestFormat
{
    private const int MaxBytes = 1024 * 1024;

    public static byte[] Write(BackupManifest manifest)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", manifest.Format);
            writer.WriteString("paduraksa", manifest.PaduraksaVersion);
            writer.WriteString("createdAt", manifest.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartObject("databases");
            WriteList(writer, "config", manifest.ConfigMigrations);
            WriteList(writer, "vault", manifest.VaultMigrations);
            writer.WriteEndObject();
            writer.WriteStartArray("entries");
            foreach (var entry in manifest.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteNumber("bytes", entry.Bytes);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("kdf");
            writer.WriteString("algorithm", manifest.Kdf.Algorithm);
            writer.WriteNumber("version", manifest.Kdf.Version);
            writer.WriteNumber("memoryKiB", manifest.Kdf.MemoryKiB);
            writer.WriteNumber("iterations", manifest.Kdf.Iterations);
            writer.WriteNumber("parallelism", manifest.Kdf.Parallelism);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <exception cref="BackupException"><see cref="BackupFailure.Damaged"/>: not a manifest this build can read.</exception>
    public static BackupManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            throw Damaged("it is too large");
        }

        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            var format = root.GetProperty("format").GetInt32();
            if (format != BackupHeader.CurrentFormat)
            {
                throw Damaged($"format {format}");
            }

            var databases = root.GetProperty("databases");
            var kdf = root.GetProperty("kdf");
            return new BackupManifest(
                format,
                root.GetProperty("paduraksa").GetString() ?? throw Damaged("no version"),
                DateTimeOffset.Parse(root.GetProperty("createdAt").GetString() ?? throw Damaged("no date"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Strings(databases.GetProperty("config")),
                Strings(databases.GetProperty("vault")),
                [.. root.GetProperty("entries").EnumerateArray().Select(e => new BackupEntry(
                    e.GetProperty("name").GetString() ?? throw Damaged("an entry has no name"),
                    e.GetProperty("bytes").GetInt64(),
                    e.GetProperty("sha256").GetString() ?? throw Damaged("an entry has no hash")))],
                new BackupKdf(
                    kdf.GetProperty("algorithm").GetString() ?? throw Damaged("no algorithm"),
                    kdf.GetProperty("version").GetInt32(),
                    kdf.GetProperty("memoryKiB").GetInt32(),
                    kdf.GetProperty("iterations").GetInt32(),
                    kdf.GetProperty("parallelism").GetInt32()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw Damaged(ex.Message);
        }
    }

    private static List<string> Strings(JsonElement array) =>
        [.. array.EnumerateArray().Select(e => e.GetString() ?? throw Damaged("a migration has no name"))];

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static BackupException Damaged(string why) => new(BackupFailure.Damaged, $"The backup's manifest is not readable ({why}).");
}
