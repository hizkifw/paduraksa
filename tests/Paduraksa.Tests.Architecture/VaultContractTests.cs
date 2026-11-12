using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Architecture.Fixtures;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <remarks>
/// <para>
/// <b>Deny by default in <c>Paduraksa.Core.Vault</c>.</b> A public member there may use only: typed IDs, enums, primitives
/// and <c>string</c>, <c>DateTimeOffset</c>, <c>TimeSpan</c>, <c>Uri</c>, <c>CancellationToken</c>, the (non-delegate)
/// types of that namespace, <c>Task</c>/<c>ValueTask</c>/<c>Nullable</c>/<c>IReadOnlyList</c> of those, and
/// <c>ReadOnlySpan&lt;byte&gt;</c>, <c>ReadOnlyMemory&lt;byte&gt;</c> and <c>ReadOnlyMemory&lt;char&gt;</c> as plain inbound
/// parameters. Everything else fails: delegates, <c>Span</c>/<c>Memory</c> and <c>IBufferWriter</c> as an output, <c>Stream</c>,
/// <c>object</c>, generic methods, arrays, <c>IReadOnlyList&lt;byte&gt;</c>, certificate and key types. That covers records too (their
/// properties and constructors are members like any other); text members (egress) are also refused under key-like names.
/// The only exceptions are the explicit <see cref="Allowed"/> list, one entry per sanctioned position, each with the reason it
/// is safe.
/// </para>
/// <para>
/// The rest of Core (typed IDs, model) is held to the narrower rule that no public member names a key or certificate type.
/// </para>
/// </remarks>
public sealed class VaultContractTests
{
    private const string VaultNamespace = "Paduraksa.Core.Vault";

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The capability interfaces the vault exposes.</summary>
    public static readonly Type[] CapabilityInterfaces =
    [
        typeof(IServerCertificateSource),
        typeof(IClientCertificateSource),
        typeof(ICertificateIssuance),
        typeof(ICertificateImport),
        typeof(ICertificateCatalog),
        typeof(ICertificateNameRules),
        typeof(IJwsSigner),
        typeof(IMacKey),
        typeof(IMacKeySource),
        typeof(ISecretStore),
        typeof(ISecretHttpHandlerFactory),
        typeof(INotificationSecretSource),
        typeof(IVaultLifecycle),
        typeof(IVaultKekRotation),
        typeof(IVaultBackup),
        typeof(IStagedBackup),
        typeof(ITotpAuthenticator),
        typeof(IDataProtectionKeyStore),
        typeof(IPasswordCredential),
        typeof(IPasswordCredentials),
        typeof(IPasswordVerifier),
    ];

    /// <summary>
    /// The positions in <c>Paduraksa.Core.Vault</c> that the deny-by-default rule lets through, as <c>Type.Member</c> (a return
    /// value, property or constructor) or <c>Type.Member(parameter)</c>. Each is a deliberate decision.
    /// </summary>
    private static readonly HashSet<string> Allowed =
    [
        // The two sanctioned certificate egresses: Kestrel and the upstream SocketsHttpHandler need the key in-process.
        $"{nameof(IServerCertificateSource)}.{nameof(IServerCertificateSource.LoadForServingAsync)}",
        $"{nameof(IClientCertificateSource)}.{nameof(IClientCertificateSource.LoadForUpstreamAsync)}",

        // The vault-owned transport: the one return that is not plain data (it holds the credential inside the vault).
        $"{nameof(ISecretHttpHandlerFactory)}.{nameof(ISecretHttpHandlerFactory.CreateAuthenticatingHandler)}",

        // The fourth egress, the narrowest: a notification channel's webhook URL, token or SMTP password has to be used by its sender. Only
        // secrets of kind NotificationSecret open (any other kind answers as if absent), the value is a view of the vault's pinned buffer that is
        // zeroed when the callback returns, and nothing returns it.
        $"{nameof(INotificationSecretSource)}.{nameof(INotificationSecretSource.UseAsync)}(use)",

        // The upload goes in as a stream (read-only use, size-capped), the one way a PEM or PFX enters.
        $"{nameof(ICertificateImport)}.{nameof(ICertificateImport.ImportAsync)}(pemOrPfx)",

        // A MAC is the only operation that has to write bytes into the caller's buffer; the tag is public.
        $"{nameof(IMacKey)}.{nameof(IMacKey.Sign)}(tag)",

        // Public data: a PKCS#10 request and PEM certificates (no private key can be in them).
        $"{nameof(CsrResult)}.{nameof(CsrResult.Der)}",
        $"{nameof(CsrResult)}.{nameof(CsrResult.Pem)}",
        $"{nameof(CertificateInfo)}.{nameof(CertificateInfo.PublicChainPem)}",

        // A backup is a stream the passphrase has sealed (AES-256-GCM under an Argon2id key, the master key never in it): what goes out is
        // ciphertext, and what comes in is read once, sealed, and checked before any of it is used.
        $"{nameof(IVaultBackup)}.{nameof(IVaultBackup.CreateAsync)}(destination)",
        $"{nameof(IVaultBackup)}.{nameof(IVaultBackup.OpenAsync)}(archive)",

        // The third sanctioned egress: Data Protection must parse and use the key element in-process.
        $"{nameof(DataProtectionKey)}.{nameof(DataProtectionKey.KeyXml)}",
    ];

    /// <summary>Text members under these names are key material on their way out.</summary>
    private static readonly Regex KeyLikeName = new(
        "private|pkcs8|pfx|pkcs12|secret|passw|pem|key(?!alg|spec|type|ident)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static TheoryData<Type> Interfaces => [.. CapabilityInterfaces];

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void CapabilityInterfaceIsPublicInVaultNamespace(Type type)
    {
        Assert.True(type.IsInterface && type.IsPublic);
        Assert.Equal(VaultNamespace, type.Namespace);
    }

    [Fact]
    public void EveryPublicInterfaceInCoreVaultNamespaceIsListed()
    {
        var declared = VaultTypes().Where(t => t.IsInterface).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        // A new capability interface must be added here too, so that it and these checks change together.
        Assert.Equal(CapabilityInterfaces.OrderBy(t => t.Name, StringComparer.Ordinal), declared);
    }

    [Fact]
    public void VaultContractsExposeNothingButPlainDataAndTheAllowedPositions()
    {
        var leaks = VaultLeaks(VaultTypes());
        Assert.True(leaks.Count == 0, "Not allowed in the vault contracts (deny by default): " + string.Join("; ", leaks));
    }

    [Fact]
    public void PasswordVerifiersAndTheirCacheNeverLeaveTheVault()
    {
        // The hash type, the parsed form, the cache, the registry, the handle and the store are what hold a verifier or a cache entry; each is internal to the
        // vault (the vault exports nothing but its registration, see ArchRules.VaultHasNoPublicTypes), and no other assembly declares
        // its own copy.
        var vault = LoadAssembly(Layer.Vault);
        string[] names = ["PasswordHash", "ParsedPasswordHash", "PasswordVerificationCache", "PasswordRegistry", "PasswordCredentialSlot", "PasswordCredentialStore"];
        foreach (var name in names)
        {
            var type = Assert.Single(vault.GetTypes(), t => t.Name == name);
            Assert.False(type.IsVisible, $"{name} must be internal to the vault");
        }

        foreach (var layer in SolutionLayers.All.Where(l => l != Layer.Vault))
        {
            Assert.DoesNotContain(LoadAssembly(layer).GetTypes(), t => names.Contains(t.Name));
        }

        // What crosses the boundary is a handle, a verdict and the probe's two facts: none can carry a verifier or a cache key.
        Assert.All(
            typeof(PasswordCacheProbe).GetProperties().Select(p => p.PropertyType).Concat(typeof(IPasswordCredential).GetProperties().Select(p => p.PropertyType)),
            t => Assert.True(t == typeof(bool) || t == typeof(long) || t == typeof(PasswordCredentialId)));
    }

    [Fact]
    public void RestOfCoreNamesNoKeyOrCertificateType()
    {
        var types = LoadAssembly(Layer.Core).GetExportedTypes().Where(t => t.Namespace != VaultNamespace);
        var leaks = KeyTypeLeaks(types);
        Assert.True(leaks.Count == 0, "Key or certificate types in Core's public surface: " + string.Join("; ", leaks));
    }

    [Fact]
    public void AllowedPositionsAreStillThere()
    {
        // An allowlist entry that matches nothing is stale (a rename would otherwise silently drop its protection).
        var positions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in VaultTypes())
        {
            foreach (var member in type.GetMembers(Declared))
            {
                positions.Add($"{type.Name}.{member.Name}");
                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        positions.Add($"{type.Name}.{member.Name}({parameter.Name})");
                    }
                }
            }
        }

        Assert.DoesNotContain(Allowed, a => !positions.Contains(a));
    }

    [Fact]
    public void SanityRulesFlagEveryLeakShape()
    {
        var strict = VaultLeaks([typeof(LeakyKeyHolder), typeof(CleanKeyHolder)], allowed: []);
        foreach (var member in new[]
        {
            "RsaKey", "Ec", "Cert", "Context", "PrivateKey", "KeyPem", "GetKeyAsync", "ExportPfx", "Fill", "Accept", "Parameters", "Collection",
            "Callback", "Register", "WriteKey", "ExportTo", "ExportTo2", "GetSecretAsync", "Password", "GetBlob", "Pem", "Get", "Key", "PrivateKeyBytes",
        })
        {
            Assert.Contains(strict, l => l.StartsWith($"LeakyKeyHolder.{member} ", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(strict, l => l.Contains("Hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(strict, l => l.StartsWith("CleanKeyHolder", StringComparison.Ordinal));

        // Without its allowlist entries the real contracts would fail: the entries are what lets each position through.
        Assert.Equal(
            Allowed.Select(a => a.Split('.')[0]).Distinct().Order(),
            VaultLeaks(VaultTypes(), allowed: []).Select(l => l.Split('.')[0]).Distinct().Order());

        var narrow = KeyTypeLeaks([typeof(LeakyKeyHolder), typeof(CleanKeyHolder)]);
        foreach (var member in new[] { "RsaKey", "Ec", "Cert", "Context", "Accept", "Parameters", "Collection" })
        {
            Assert.Contains(narrow, l => l.StartsWith($"LeakyKeyHolder.{member} ", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(narrow, l => l.StartsWith("CleanKeyHolder", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyMaterialEgressTypesDoNotPrintTheirKey()
    {
        // The one public egress type that carries raw key material (the Data Protection element): a record's generated
        // ToString would print every property, including KeyXml. The others in the allowlist are public data (a CSR, PEM
        // certificate chains) or framework types.
        DataProtectionKey key = new("friendly-name", "SECRET-KEY-MATERIAL");

        Assert.DoesNotContain("SECRET-KEY-MATERIAL", key.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-KEY-MATERIAL", $"{key}", StringComparison.Ordinal);
        Assert.Contains("FriendlyName", key.ToString(), StringComparison.Ordinal);
    }

    private static List<Type> VaultTypes() =>
    [
        .. LoadAssembly(Layer.Core).GetExportedTypes().Where(t => t.Namespace == VaultNamespace),
    ];

    private static List<string> VaultLeaks(IEnumerable<Type> types) => VaultLeaks(types, Allowed);

    /// <summary>The deny-by-default rule (see the class remarks) over <paramref name="types"/>.</summary>
    private static List<string> VaultLeaks(IEnumerable<Type> types, HashSet<string> allowed)
    {
        var leaks = new List<string>();
        foreach (var type in types.Where(t => t.IsVisible && !t.IsEnum && !t.IsDefined(typeof(CompilerGeneratedAttribute))))
        {
            void Check(string member, Type exposed, bool inbound, string? parameter = null)
            {
                var position = parameter is null ? $"{type.Name}.{member}" : $"{type.Name}.{member}({parameter})";
                if (allowed.Contains(position))
                {
                    return;
                }

                var violation = Violation(exposed, inbound);
                if (violation is null && !inbound && IsText(exposed) && KeyLikeName.IsMatch(parameter ?? member))
                {
                    violation = "a text value under a key-like name";
                }

                if (violation is not null)
                {
                    leaks.Add($"{type.Name}.{member} {(parameter is null ? "" : $"(parameter '{parameter}') ")}exposes {violation}");
                }
            }

            foreach (var member in type.GetMembers(Declared).Where(m => IsVisibleOutside(m) && !IsGenerated(m)))
            {
                switch (member)
                {
                    case PropertyInfo p:
                        Check(p.Name, p.PropertyType, inbound: false);
                        break;
                    case FieldInfo f:
                        Check(f.Name, f.FieldType, inbound: false);
                        break;
                    case EventInfo e:
                        Check(e.Name, e.EventHandlerType!, inbound: false);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            if (mi.IsGenericMethodDefinition)
                            {
                                leaks.Add($"{type.Name}.{m.Name} is a generic method");
                            }

                            Check(m.Name, mi.ReturnType, inbound: false);
                        }

                        foreach (var parameter in m.GetParameters())
                        {
                            Check(m.Name, parameter.ParameterType, inbound: true, parameter.Name);
                        }

                        break;
                }
            }
        }

        return leaks;
    }

    /// <summary>Why <paramref name="type"/> may not appear in this position, or null if it may.</summary>
    private static string? Violation(Type type, bool inbound)
    {
        if (type.IsByRef)
        {
            return "an out or ref parameter";
        }

        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask))
        {
            return null;
        }

        if (type.IsGenericParameter)
        {
            return "a generic type parameter";
        }

        if (type.IsArray)
        {
            return $"an array ({type.Name})";
        }

        if (type.IsAssignableTo(typeof(Delegate)))
        {
            return $"a delegate ({type.Name})";
        }

        if (inbound && (type == typeof(ReadOnlySpan<byte>) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(ReadOnlyMemory<char>)))
        {
            return null;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>) || definition == typeof(Nullable<>)
                || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
            {
                return Violation(type.GetGenericArguments()[0], inbound: false);
            }

            return $"{type.Name} (type not allowed here)";
        }

        return IsPlainLeaf(type) ? null : $"{type.FullName} (type not allowed here)";
    }

    private static bool IsPlainLeaf(Type type) =>
        type.IsEnum
        || type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(string)
        || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(CancellationToken)
        || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITypedId<>))
        || (type.Namespace == VaultNamespace && !type.IsAssignableTo(typeof(Delegate)));

    private static bool IsText(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var d && (d == typeof(Task<>) || d == typeof(ValueTask<>)))
        {
            type = type.GetGenericArguments()[0];
        }

        return type == typeof(string);
    }

    /// <summary>The narrow rule for the rest of Core: no public member names a key or certificate type.</summary>
    private static List<string> KeyTypeLeaks(IEnumerable<Type> types)
    {
        var leaks = new List<string>();
        foreach (var type in types.Where(t => t.IsVisible))
        {
            void Check(string member, Type? exposed)
            {
                if (exposed is not null && Unwrap(exposed).FirstOrDefault(IsKeyType) is { } key)
                {
                    leaks.Add($"{type.Name}.{member} exposes {key.FullName}");
                }
            }

            foreach (var member in type.GetMembers(Declared).Where(m => IsVisibleOutside(m) && !IsGenerated(m)))
            {
                switch (member)
                {
                    case PropertyInfo p:
                        Check(p.Name, p.PropertyType);
                        break;
                    case FieldInfo f:
                        Check(f.Name, f.FieldType);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            Check(m.Name, mi.ReturnType);
                        }

                        foreach (var parameter in m.GetParameters())
                        {
                            Check(m.Name, parameter.ParameterType);
                        }

                        break;
                }
            }
        }

        return leaks;
    }

    /// <summary>The type itself and everything it is built from: element types, generic arguments.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType)
        {
            return Unwrap(type.GetElementType()!);
        }

        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Unwrap).Prepend(type) : [type];
    }

    private static bool IsKeyType(Type type) =>
        type.IsAssignableTo(typeof(AsymmetricAlgorithm))
        || type.IsAssignableTo(typeof(SymmetricAlgorithm))
        || IsDerivedFromByName(type, "System.Security.Cryptography.KeyedHashAlgorithm")
        || type.IsAssignableTo(typeof(X509Certificate))
        || type.IsAssignableTo(typeof(X509CertificateCollection))
        || type == typeof(SslStreamCertificateContext)
        || type == typeof(RSAParameters)
        || type == typeof(ECParameters)
        || type == typeof(DSAParameters)
        || type.FullName is "System.Security.Cryptography.AesGcm" or "System.Security.Cryptography.ChaCha20Poly1305"
        || type == typeof(CngKey);

    /// <summary>By name, because the ban list forbids naming the keyed-hash base type in this project.</summary>
    private static bool IsDerivedFromByName(Type type, string fullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenerated(MemberInfo member) =>
        member.IsDefined(typeof(CompilerGeneratedAttribute))
        || member.Name is "Equals" or "GetHashCode" or "ToString";

    private static bool IsVisibleOutside(MemberInfo member) => member switch
    {
        PropertyInfo p => p.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
        MethodBase m => (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly)
            && (m is ConstructorInfo || !m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)),
        _ => false,
    };
}
