// Fixtures for the reflection-based rules (typed IDs, Data exposure, key-material exposure). NOT production code.
using System.Buffers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Paduraksa.Data.ArchFixtures;

namespace Paduraksa.Tests.Architecture.Fixtures;

/// <summary>Stand-in for a generated typed ID (the real ones implement Paduraksa.Core.Ids.ITypedId).</summary>
public readonly record struct FixtureTypedId(Guid Value);

public sealed record UntypedIdDto(Guid Id, string TenantId, long? RouteId, int HostId);

public sealed record TypedIdDto(FixtureTypedId Id, FixtureTypedId? ParentId, string Name, bool IsValid, Guid Token);

public sealed class LeakyDto(FakeData? entity = null)
{
    public FakeData? Entity { get; set; } = entity;

    public IReadOnlyList<FakeData> Entities { get; set; } = [];

    public FakeData[] EntityArray { get; set; } = [];

    public void Accept(FakeData value) => Entity = value;

    public FakeData? Produce() => Entity;

    public Task<FakeData?> ProduceAsync() => Task.FromResult(Entity);

    // Non-public members may use Data types without leaking them.
    internal FakeData? Hidden { get; set; }
}

public sealed record CleanDto(string Name, IReadOnlyList<int> Values);

/// <summary>A callback type that could be handed key bytes.</summary>
public delegate void KeySink(ReadOnlySpan<byte> key);

/// <summary>Every way a public member can hand out (or take in) key material. The rules must flag all of them.</summary>
public sealed class LeakyKeyHolder
{
    public RSA? RsaKey { get; set; }

    public static Task<ECDsa> Ec() => Task.FromResult(ECDsa.Create());

    public X509Certificate2? Cert { get; set; }

    public static ValueTask<SslStreamCertificateContext?> Context() => default;

    public byte[] PrivateKey { get; set; } = [];

    public string KeyPem { get; set; } = "";

    public static ValueTask<ReadOnlyMemory<byte>> GetKeyAsync() => default;

    public static string ExportPfx() => "";

    public static void Fill(out byte[] secret) => secret = [];

    public static void Accept(HMACSHA256 mac) => _ = mac;

    public RSAParameters Parameters { get; set; }

    public X509Certificate2Collection? Collection { get; set; }

    public static void Callback(Action<byte[]> onKey) => _ = onKey;

    public static void Register(KeySink sink) => _ = sink;

    public static void WriteKey(Span<byte> destination) => destination.Clear();

    public static void ExportTo(Stream destination) => _ = destination;

    public static void ExportTo2(IBufferWriter<byte> destination) => _ = destination;

    public static ValueTask<ReadOnlyMemory<char>> GetSecretAsync() => default;

    public char[] Password { get; set; } = [];

    public static ReadOnlyMemory<byte> GetBlob() => default;

    public string Pem { get; set; } = "";

    public static T Get<T>() => default!;

    public object? Key { get; set; }

    public IReadOnlyList<byte> PrivateKeyBytes { get; set; } = [];

    // Non-public members may use key types without leaking them.
    internal RSA? Hidden { get; set; }
}

/// <summary>Public data and ingress of a secret value are fine: nothing here can carry key material out.</summary>
public sealed class CleanKeyHolder
{
    public string KeyAlgorithm { get; set; } = "ECDSA P-256";

    public int Count { get; set; }

    public static ValueTask Put(ReadOnlyMemory<byte> value) => default;

    public static bool Verify(ReadOnlySpan<byte> tag) => tag.IsEmpty;
}
