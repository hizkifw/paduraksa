using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>
/// Every field of <see cref="HostSettings"/> through the one merge: set only at the global defaults, only at
/// the profile, only at the host; overridden at each level; and the built-in default when no layer sets it. It runs the
/// real path - the validator accepts each document, and the mapping, which is what the compiler compiles, resolves it.
/// <para><b>Adding a setting</b> is one row here: a field accessor, what a layer that sets it looks like, the value the
/// compiled host then carries, the built-in default, and two sample values.</para>
/// </summary>
public sealed class SettingsInheritanceTests
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);

    /// <summary>Every field of the contract: its layers and what the resolved host says.</summary>
    private static readonly Field[] Fields =
    [
        new(
            "forceHttps",
            static s => s.ForceHttps?.ToString().ToLowerInvariant(),
            static v => new HostSettings { ForceHttps = bool.Parse(v) },
            static h => h.ForceHttps ? "true" : "false",
            BuiltIn: "false",
            First: "true",
            Second: "false"),
        new(
            "tls.minimumVersion",
            static s => s.Tls?.MinimumVersion?.ToString(),
            static v => new HostSettings { Tls = new TlsSettings { MinimumVersion = Enum.Parse<TlsMinimumVersion>(v) } },
            static h => h.Tls.MinimumVersion.ToString(),
            BuiltIn: "Tls12",
            First: "Tls13",
            Second: "Tls12"),
        new(
            "tls.tcpAlpn",
            static s => s.Tls?.TcpAlpn?.ToString(),
            static v => new HostSettings { Tls = new TlsSettings { TcpAlpn = Enum.Parse<TcpAlpn>(v) } },
            static h => h.Tls.TcpAlpn.ToString(),
            BuiltIn: "Http2AndHttp11",
            First: "Http11Only",
            Second: "Http2AndHttp11"),
        new(
            "tls.http3",
            static s => s.Tls?.Http3?.ToString().ToLowerInvariant(),
            static v => new HostSettings { Tls = new TlsSettings { Http3 = bool.Parse(v) } },
            static h => h.Tls.Http3 ? "true" : "false",
            BuiltIn: "true",
            First: "false",
            Second: "true"),
        new(
            "ipAllowList",
            static s => s.IpAllowList is { } list ? string.Join(' ', list.Addresses) : null,
            static v => new HostSettings { IpAllowList = new IpAllowListSettings([.. v.Split(' ')]) },
            static h => h.Policies.OfType<IpAllowListPolicySpec>().FirstOrDefault() is { } list ? string.Join(' ', list.Addresses) : null,
            BuiltIn: null,
            First: "10.0.0.0/8",
            Second: "192.168.1.0/24"),
        new(
            "hsts.enabled",
            static s => s.Hsts?.Enabled?.ToString().ToLowerInvariant(),
            static v => new HostSettings { Hsts = new HstsSettings { Enabled = bool.Parse(v) } },
            static h => h.Hsts.Enabled ? "true" : "false",
            BuiltIn: "false",
            First: "true",
            Second: "false"),
        new(
            "hsts.maxAge",
            static s => s.Hsts?.MaxAge?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            static v => new HostSettings { Hsts = new HstsSettings { MaxAge = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) } },
            static h => h.Hsts.MaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BuiltIn: "31536000",
            First: "86400",
            Second: "0"),
        new(
            "hsts.includeSubDomains",
            static s => s.Hsts?.IncludeSubDomains?.ToString().ToLowerInvariant(),
            static v => new HostSettings { Hsts = new HstsSettings { IncludeSubDomains = bool.Parse(v) } },
            static h => h.Hsts.IncludeSubDomains ? "true" : "false",
            BuiltIn: "false",
            First: "true",
            Second: "false"),
        new(
            "hsts.preload",
            static s => s.Hsts?.Preload?.ToString().ToLowerInvariant(),
            // Preload on its own isn't a valid layer: it needs includeSubDomains and a year of max-age.
            static v => new HostSettings
            {
                Hsts = bool.Parse(v)
                    ? new HstsSettings { Preload = true, IncludeSubDomains = true, MaxAge = 31_536_000 }
                    : new HstsSettings { Preload = false },
            },
            static h => h.Hsts.Preload ? "true" : "false",
            BuiltIn: "false",
            First: "true",
            Second: "false"),
        new(
            "accessLog",
            static s => s.AccessLog?.ToString().ToLowerInvariant(),
            static v => new HostSettings { AccessLog = bool.Parse(v) },
            static h => h.AccessLog ? "true" : "false",
            BuiltIn: "true",
            First: "false",
            Second: "true"),
        new(
            "requestIdPropagation",
            static s => s.RequestIdPropagation?.ToString(),
            static v => new HostSettings { RequestIdPropagation = Enum.Parse<RequestIdPropagation>(v) },
            static h => h.RequestIdPropagation.ToString(),
            BuiltIn: "UpstreamAndResponse",
            First: "Off",
            Second: "ResponseOnly"),
        new(
            "serverHeader.mode",
            static s => s.ServerHeader?.Mode?.ToString(),
            // The custom mode needs its value, or the document isn't valid.
            static v => new HostSettings
            {
                ServerHeader = Enum.Parse<ServerHeaderMode>(v) == ServerHeaderMode.Custom
                    ? new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" }
                    : new ServerHeaderSettings { Mode = Enum.Parse<ServerHeaderMode>(v) },
            },
            static h => h.ServerHeader.Mode.ToString(),
            BuiltIn: "Paduraksa",
            First: "Custom",
            Second: "Remove"),
        new(
            "serverHeader.value",
            static s => s.ServerHeader?.Value,
            static v => new HostSettings { ServerHeader = new ServerHeaderSettings { Value = v } },
            static h => h.ServerHeader.Value,
            BuiltIn: null,
            First: "edge/1.0",
            Second: "edge/2.0"),
        new(
            "proofOfWork.enabled",
            static s => s.ProofOfWork?.Enabled?.ToString().ToLowerInvariant(),
            static v => new HostSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = bool.Parse(v) } },
            static h => h.ProofOfWork.Enabled ? "true" : "false",
            BuiltIn: "false",
            First: "true",
            Second: "false"),
        new(
            "proofOfWork.difficulty",
            static s => s.ProofOfWork?.Difficulty?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            static v => new HostSettings { ProofOfWork = new ProofOfWorkSettings { Difficulty = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) } },
            static h => h.ProofOfWork.Difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BuiltIn: "16",
            First: "20",
            Second: "12"),
        new(
            "proofOfWork.tokenLifetimeHours",
            static s => s.ProofOfWork?.TokenLifetimeHours?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            static v => new HostSettings { ProofOfWork = new ProofOfWorkSettings { TokenLifetimeHours = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) } },
            static h => h.ProofOfWork.TokenLifetimeHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BuiltIn: "168",
            First: "24",
            Second: "720"),
    ];

    public static TheoryData<string> FieldNames => [.. Fields.Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void EveryFieldResolvesThroughTheLayers(string name)
    {
        var field = Fields.Single(f => f.Name == name);

        // Set at one layer only: that layer's value reaches the host.
        AssertResolved(field, field.First, defaults: field.First, profile: null, host: null);
        AssertResolved(field, field.First, defaults: null, profile: field.First, host: null);
        AssertResolved(field, field.First, defaults: null, profile: null, host: field.First);

        // Overridden at each level: the higher layer wins, field by field.
        AssertResolved(field, field.Second, defaults: field.First, profile: field.Second, host: null);
        AssertResolved(field, field.Second, defaults: field.First, profile: field.First, host: field.Second);
        AssertResolved(field, field.Second, defaults: field.First, profile: null, host: field.Second);

        // No layer sets it: the built-in default, which one place states.
        AssertResolved(field, field.BuiltIn, defaults: null, profile: null, host: null);
    }

    [Fact]
    public void TheRowsCoverTheWholeContract()
    {
        // Walked from the contract itself, nested settings records included: a field added to HostSettings has to get its
        // row here, or this test names it.
        List<string> contract = [];
        foreach (var property in typeof(HostSettings).GetProperties())
        {
            if (typeof(ISettingsLayer).IsAssignableFrom(property.PropertyType))
            {
                foreach (var nested in property.PropertyType.GetProperties().Where(p => p.Name != nameof(ISettingsLayer.SetsNothing)))
                {
                    contract.Add($"{Camel(property.Name)}.{Camel(nested.Name)}");
                }

                continue;
            }

            contract.Add(Camel(property.Name));
        }

        Assert.Equal(contract, Fields.Select(f => f.Name));

        static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static TheoryData<string> NestedNames => [.. NestedCases.Select(c => c.Name)];

    /// <summary>
    /// Fields of one nested record at three different layers: the merge has to keep every one of them, not just the
    /// highest layer's. The per-field rows above can't catch a nested merge that returns the upper record whole, because
    /// each of them sets one field.
    /// </summary>
    [Theory]
    [MemberData(nameof(NestedNames))]
    public void EveryFieldOfANestedRecordKeepsItsOwnLayer(string name)
    {
        var nested = NestedCases.Single(c => c.Name == name);
        var defaults = LayersOf(nested.Defaults);
        var profile = LayersOf(nested.Profile);
        var host = LayersOf(nested.Host);

        var document = Document(defaults, profile, host);
        Assert.Empty(ConfigValidator.Validate(document));
        var mapped = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty);
        Assert.Empty(mapped.Errors);
        var compiled = Assert.Single(mapped.Model.Hosts);
        Assert.True(SnapshotCompiler.Compile(mapped.Model).Succeeded);

        // Every field the case places is still the value it was given, wherever its layer sits.
        foreach (var field in nested.Fields)
        {
            Assert.Equal(SampleOf(field), field.Resolved(compiled));
        }

        // The dashboard binds the same contract directly, so its fields fold what they inherit through this merge too; its own
        // table (HostSettingsFieldsTests, Tests.Dashboard) walks every field, and keeps the built-in defaults out of the form.
    }

    [Fact]
    public void EveryNestedRecordHasANestedMergeCase()
    {
        // A new nested settings record gets its case here, the same way a new field gets its row: the walk starts at the
        // contract, so the field can't be forgotten.
        foreach (var (name, type, fields) in NestedRecords())
        {
            var nested = Assert.Single(NestedCases, c => c.Name == name);
            Assert.Equal(fields.Order(StringComparer.Ordinal), nested.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));
            Assert.All(nested.Fields, field => Assert.Equal(type, FieldTypeOf(field)));
        }

        Assert.Equal(NestedRecords().Select(r => r.Name).Order(StringComparer.Ordinal), NestedCases.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryNestedRecordKnowsWhenItSetsNothing()
    {
        // The one rule canonical JSON drops an empty layer with: each record's conjunction has to name every field, so a
        // field added without it fails here rather than silently vanishing from stored documents.
        foreach (var (name, type, fields) in NestedRecords())
        {
            var property = typeof(HostSettings).GetProperties().Single(p => p.PropertyType == type);
            var empty = (ISettingsLayer)Activator.CreateInstance(type)!;
            Assert.True(empty.SetsNothing, $"{type.Name} with no field set must set nothing");

            foreach (var fieldName in fields)
            {
                var field = FieldOf(fieldName);
                var layer = field.Layer(SampleOf(field));
                var record = (ISettingsLayer)property.GetValue(layer)!;
                Assert.False(record.SetsNothing, $"{fieldName} is set, so {type.Name}.SetsNothing must be false");

                // And the canonical document writes the record, so the rule keeps what a document that sets it says.
                var json = System.Text.Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(Document(layer, null, null)));
                Assert.Contains($"\"{name}\":", json, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Every nested settings record of the contract, with its property name and field names.</summary>
    private static IEnumerable<(string Name, Type Type, string[] Fields)> NestedRecords() =>
        typeof(HostSettings).GetProperties()
            .Where(p => typeof(ISettingsLayer).IsAssignableFrom(p.PropertyType))
            .Select(p => (
                char.ToLowerInvariant(p.Name[0]) + p.Name[1..],
                p.PropertyType,
                p.PropertyType.GetProperties()
                    .Where(f => f.Name != nameof(ISettingsLayer.SetsNothing))
                    .Select(f => $"{char.ToLowerInvariant(p.Name[0]) + p.Name[1..]}.{char.ToLowerInvariant(f.Name[0]) + f.Name[1..]}")
                    .ToArray()));

    private static Field FieldOf(string name) => Fields.Single(f => f.Name == name);

    private static Type FieldTypeOf(Field field) => typeof(HostSettings).GetProperties()
        .Single(p => field.Name.StartsWith(char.ToLowerInvariant(p.Name[0]) + p.Name[1..] + ".", StringComparison.Ordinal))
        .PropertyType;

    /// <summary>
    /// The sample a field is set to when several fields of one record share a document: a field whose first sample drags
    /// others with it (preload needs includeSubDomains and a year; the custom mode needs its value) uses its second,
    /// which stands alone.
    /// </summary>
    private static string SampleOf(Field field)
    {
        var alone = field.Layer(field.First);
        var nested = field.Name[..field.Name.IndexOf('.')];
        var setsOthers = Fields
            .Where(f => f.Name.StartsWith(nested + ".", StringComparison.Ordinal) && f != field)
            .Any(f => f.Read(alone) is not null);
        return setsOthers ? field.Second : field.First;
    }

    /// <summary>The layer that sets those fields, each to its sample.</summary>
    private static HostSettings LayersOf(IEnumerable<string> fields)
    {
        HostSettings layer = new();
        foreach (var name in fields)
        {
            layer = HostSettingsLayers.Merge(layer, FieldOf(name).Layer(SampleOf(FieldOf(name))));
        }

        return layer;
    }

    /// <summary>
    /// The document with those layers, through the validator and the mapping: it must be valid, and the compiled host
    /// must carry <paramref name="expected"/>.
    /// </summary>
    private static void AssertResolved(Field field, string? expected, string? defaults, string? profile, string? host)
    {
        // The table describes itself: a layer built for a sample really sets that field to it.
        Assert.Equal(defaults, defaults is null ? null : field.Read(field.Layer(defaults)));
        Assert.Equal(profile, profile is null ? null : field.Read(field.Layer(profile)));
        Assert.Equal(host, host is null ? null : field.Read(field.Layer(host)));

        var document = Document(
            defaults: defaults is null ? null : field.Layer(defaults),
            profile: profile is null ? null : field.Layer(profile),
            host: host is null ? null : field.Layer(host));

        Assert.Empty(ConfigValidator.Validate(document));

        var mapped = ProxyConfigMapping.Map(document, Revision, CertificateInventory.Empty);
        Assert.Empty(mapped.Errors);
        var compiled = Assert.Single(mapped.Model.Hosts);

        // The compiler accepts what the mapping produced, and the host it compiled says what the merge resolved.
        Assert.True(SnapshotCompiler.Compile(mapped.Model).Succeeded);
        Assert.Equal(expected, field.Resolved(compiled));
    }

    // One proxy host under one profile under the global defaults, and nothing else to inherit from.
    private static ConfigDocument Document(HostSettings? defaults, HostSettings? profile, HostSettings? host) => new(
        Settings: new GlobalSettings(Defaults: defaults),
        Profiles: profile is null ? [] : [new ProfileConfig(LanProfile, "profile", Settings: profile)],
        Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
        Hosts: [new HostConfig(AppHost, ["app.example.com"], ProfileId: profile is null ? null : LanProfile, Settings: host, Routes: [new RouteConfig(RootRoute, AppUpstream)])]);

    /// <summary>One nested settings record and which of its fields each layer sets.</summary>
    /// <param name="Name">The record's property name, camelCase.</param>
    /// <param name="Defaults">The fields the global defaults set.</param>
    /// <param name="Profile">The fields the profile sets.</param>
    /// <param name="Host">The fields the host sets (the rest of the record, where it has more fields than layers).</param>
    private sealed record NestedCase(string Name, string[] Defaults, string[] Profile, string[] Host)
    {
        /// <summary>Every field the case places, in layer order.</summary>
        public IEnumerable<Field> Fields => Defaults.Concat(Profile).Concat(Host).Select(FieldOf);
    }

    /// <summary>
    /// One case per nested settings record: its fields spread over the three layers, which is the arrangement a broken
    /// field-by-field merge loses. `serverHeader` keeps the case an earlier test pinned by hand: the profile's value
    /// with the host's mode.
    /// </summary>
    private static readonly NestedCase[] NestedCases =
    [
        new("tls", Defaults: ["tls.minimumVersion"], Profile: ["tls.tcpAlpn"], Host: ["tls.http3"]),
        new("hsts", Defaults: ["hsts.enabled"], Profile: ["hsts.maxAge"], Host: ["hsts.includeSubDomains", "hsts.preload"]),
        new("serverHeader", Defaults: [], Profile: ["serverHeader.value"], Host: ["serverHeader.mode"]),
        new("proofOfWork", Defaults: ["proofOfWork.enabled"], Profile: ["proofOfWork.difficulty"], Host: ["proofOfWork.tokenLifetimeHours"]),
    ];

    /// <summary>
    /// One field of the contract: how a layer that sets it looks, what the compiled host then carries, its built-in
    /// default, and two sample values (the first is the more specific one, so an override is visible).
    /// </summary>
    private sealed record Field(
        string Name,
        Func<HostSettings, string?> Read,
        Func<string, HostSettings> Layer,
        Func<HostSpec, string?> Resolved,
        string? BuiltIn,
        string First,
        string Second);
}
