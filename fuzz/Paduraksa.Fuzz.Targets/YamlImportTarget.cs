using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Fuzz;

/// <summary>
/// The config YAML an operator, a script or a git repo hands the import (PLAN §11.5, "As built (M10)"): bytes the proxy didn't write, through the guarded reader, the mapping
/// onto the document and the merge, with no database. The YAML is imported twice, replacing the empty config and merging into a small stored one, so the identity matching and
/// the names that resolve against stored objects are reached too.
/// Properties: no exception, only a result with errors (each with a path that is a JSON pointer and a message that names no CLR or parser type); the same bytes give the same result
/// (the new IDs come from a counter, so even those are the same); the caps hold (the reader under a tight set of limits returns a tree that is no deeper, no larger and has no longer
/// scalar than they say, whatever aliases and anchors the input uses, and under the real limits the import neither allocates without bound nor runs long); and a document that is
/// accepted (it maps and it validates), merged or not, is exported, read back and is the same canonical bytes.
/// </summary>
internal static class YamlImportTarget
{
    private const string Header = "apiVersion: paduraksa.dev/v1\nkind: Config\n";

    // Small enough that the 4 KiB inputs libFuzzer makes reach every cap.
    private static readonly YamlLimits Tight = new(MaxBytes: 4096, MaxDepth: 6, MaxNodes: 400, MaxScalarLength: 64, MaxKeyLength: 16, MaxAnchors: 4, MaxAliases: 8);

    private static readonly Lazy<ConfigDocument> Stored = new(StoredConfig);

    public static FuzzTarget Target { get; } = new(
        "yaml-import",
        "the YAML config import: the guarded reader, the mapping onto the document, merge and replace, and export reading back to the same document",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        CheckReaderCaps(input);

        var replaced = Import(input, null, ConfigImportMode.Replace);
        var merged = Import(input, Stored.Value, ConfigImportMode.Merge);
        foreach (var (outcome, stored, mode) in new[] { (replaced, (ConfigDocument?)null, ConfigImportMode.Replace), (merged, Stored.Value, ConfigImportMode.Merge) })
        {
            CheckErrors(outcome.Result.Errors);
            if (outcome.Result.Mapped is not { } mapped)
            {
                Checks.That(outcome.Result.Errors.Count > 0, "an import with no document has no error");
                continue;
            }

            Checks.That(outcome.Result.Errors.Count == 0, "an accepted import has errors");
            var document = mode == ConfigImportMode.Merge ? ConfigImportMerge.Merge(stored!, mapped).Document : ConfigImportMerge.Replace(mapped).Document;
            CheckRoundTrip(document);
        }
    }

    private sealed record Outcome(YamlMapResult Result);

    private static Outcome Import(ReadOnlySpan<byte> input, ConfigDocument? stored, ConfigImportMode mode)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var first = ConfigYamlImporter.Read(input, stored, mode, CountingIds());
        clock.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Every alias expansion is bounded by the node budget (about 200 000 nodes), so a 4 KiB input costs tens of megabytes, never more.
        Checks.That(allocated < 192L * 1024 * 1024, $"the import of {input.Length} bytes allocated {allocated} B");
        Checks.That(clock.Elapsed < TimeSpan.FromSeconds(8), $"the import of {input.Length} bytes took {clock.Elapsed}");

        var second = ConfigYamlImporter.Read(input, stored, mode, CountingIds());
        Checks.That(Describe(first) == Describe(second), "the same bytes were imported differently the second time");
        return new Outcome(first);
    }

    // The result as text: its errors, or the canonical bytes of its document and where its nodes sit.
    private static string Describe(YamlMapResult result)
    {
        if (result.Mapped is not { } mapped)
        {
            return string.Join('\n', result.Errors.Select(static e => $"{e.Path}|{e.Code}|{e.Message}"));
        }

        var positions = string.Join(';', mapped.Positions.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => $"{p.Key}@{p.Value.Line}:{p.Value.Column}"));
        return Convert.ToHexString(SHA256.HashData(CanonicalConfigJson.Serialize(mapped.Document))) + "|" + mapped.HasSettings + "|" + positions;
    }

    private static void CheckErrors(IReadOnlyList<ConfigError> errors)
    {
        foreach (var error in errors)
        {
            Checks.That(error.Path.Length == 0 || error.Path[0] == '/', $"the error path '{error.Path}' is not a JSON pointer");
            Checks.That(error.Code.Length > 0 && error.Message.Length is > 0 and < 800, $"an error with code '{error.Code}' and {error.Message.Length} message characters");
            Checks.That(
                !error.Message.Contains("YamlDotNet", StringComparison.Ordinal) && !error.Message.Contains("System.", StringComparison.Ordinal) && !error.Message.Contains("Exception", StringComparison.Ordinal),
                $"the message names a type: {error.Message}");
        }
    }

    // Accepted (it maps, and the validator takes it), then written and read again: the same document, to the byte. A document the validator refuses (an ID used twice
    // across hosts, say) can still be written, but not read back, because the import refuses the repeat: only that it can be written is checked.
    private static void CheckRoundTrip(ConfigDocument document)
    {
        var canonical = CanonicalConfigJson.Serialize(document);
        var yaml = ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(document));
        if (ConfigValidator.Validate(document).Count > 0)
        {
            return;
        }

        Checks.That(YamlTreeReader.TryRead(Encoding.UTF8.GetBytes(yaml), YamlLimits.Default, out var tree, out var problem), $"the export doesn't read as YAML: {problem.Message}");
        Checks.That(!HasKey(tree!, "password"), "the export carries a password key");
        var again = ConfigYamlImporter.Read(Encoding.UTF8.GetBytes(yaml), null, ConfigImportMode.Replace, CountingIds());
        Checks.That(again.Mapped is not null, $"the export of an accepted document is refused: {string.Join("; ", again.Errors.Select(static e => $"{e.Path} {e.Message}"))}\n{yaml}");
        Checks.That(
            CanonicalConfigJson.Serialize(again.Mapped!.Document).AsSpan().SequenceEqual(WithoutVersions(canonical)),
            $"the export reads back as another document:\n{yaml}");
    }

    // The YAML carries no version; the document read back has every one at 0.
    private static byte[] WithoutVersions(byte[] canonical)
    {
        var document = CanonicalConfigJson.Deserialize(canonical);
        return CanonicalConfigJson.Serialize(document with
        {
            Settings = document.Settings with { Version = 0 },
            Profiles = [.. document.Profiles.Select(static p => p with { Version = 0 })],
            Upstreams = [.. document.Upstreams.Select(static u => u with { Version = 0 })],
            RateLimitPolicies = [.. document.RateLimitPolicies.Select(static p => p with { Version = 0 })],
            AccessLists = [.. document.AccessLists.Select(static l => l with { Version = 0 })],
            RuleSets = [.. document.RuleSets.Select(static r => r with { Version = 0 })],
            AuthProviders = [.. document.AuthProviders.Select(static p => p with { Version = 0 })],
            Hosts = [.. document.Hosts.Select(static h => h with { Version = 0 })],
        });
    }

    private static bool HasKey(YamlNode node, string key) => node switch
    {
        YamlSequence sequence => sequence.Items.Any(i => HasKey(i, key)),
        YamlMapping mapping => mapping.Entries.Any(e => e.Key == key || HasKey(e.Value, key)),
        _ => false,
    };

    // The reader under tight limits: whatever it accepts is within them, counted again on the tree it made.
    private static void CheckReaderCaps(ReadOnlySpan<byte> input)
    {
        var accepted = YamlTreeReader.TryRead(input, Tight, out var root, out var problem);
        var again = YamlTreeReader.TryRead(input, Tight, out var rootAgain, out var problemAgain);
        Checks.That(accepted == again && problem == problemAgain, "the reader answered differently the second time");
        if (!accepted)
        {
            Checks.That(problem.Code.StartsWith("yaml.", StringComparison.Ordinal) && problem.Message.Length > 0, $"a read error with code '{problem.Code}'");
            return;
        }

        var (depth, nodes) = Measure(root!, 1);
        Checks.That(depth <= Tight.MaxDepth, $"an accepted tree nests {depth} levels; the limit is {Tight.MaxDepth}");
        Checks.That(nodes <= Tight.MaxNodes, $"an accepted tree has {nodes} nodes; the limit is {Tight.MaxNodes}");
        Checks.That(Dump(root!) == Dump(rootAgain!), "the reader made another tree the second time");
        CheckScalars(root!);
    }

    private static (int Depth, int Nodes) Measure(YamlNode node, int level)
    {
        switch (node)
        {
            case YamlSequence sequence:
                var depth = level;
                var nodes = 1;
                foreach (var item in sequence.Items)
                {
                    var (d, n) = Measure(item, level + 1);
                    depth = Math.Max(depth, d);
                    nodes += n;
                }

                return (depth, nodes);
            case YamlMapping mapping:
                depth = level;
                nodes = 1;
                foreach (var entry in mapping.Entries)
                {
                    var (d, n) = Measure(entry.Value, level + 1);
                    depth = Math.Max(depth, d);
                    nodes += n;
                }

                return (depth, nodes);
            default:
                return (level - 1, 1);
        }
    }

    private static void CheckScalars(YamlNode node)
    {
        switch (node)
        {
            case YamlScalar scalar:
                Checks.That(scalar.Text.Length <= Tight.MaxScalarLength, $"a scalar of {scalar.Text.Length} characters; the limit is {Tight.MaxScalarLength}");
                break;
            case YamlSequence sequence:
                foreach (var item in sequence.Items)
                {
                    CheckScalars(item);
                }

                break;
            case YamlMapping mapping:
                HashSet<string> keys = new(StringComparer.Ordinal);
                foreach (var entry in mapping.Entries)
                {
                    Checks.That(entry.Key.Length <= Tight.MaxKeyLength, $"a key of {entry.Key.Length} characters; the limit is {Tight.MaxKeyLength}");
                    Checks.That(keys.Add(entry.Key), $"the key '{entry.Key}' twice in one mapping");
                    CheckScalars(entry.Value);
                }

                break;
        }
    }

    private static string Dump(YamlNode node) => node switch
    {
        YamlScalar scalar => (scalar.Plain ? "p" : "q") + scalar.Text.Length + ":" + scalar.Text,
        YamlSequence sequence => "[" + string.Join(",", sequence.Items.Select(Dump)) + "]",
        YamlMapping mapping => "{" + string.Join(",", mapping.Entries.Select(static e => e.Key.Length + ":" + e.Key + "=" + Dump(e.Value))) + "}",
        _ => "?",
    };

    // Valid IDs from a counter, so a new object gets the same ID in every run of the same bytes.
    private static Func<Type, string> CountingIds()
    {
        var next = 0;
        return type =>
        {
            var guid = new Guid($"01900000-0000-7000-8000-{50_000 + next++:x12}");
            return type.Name switch
            {
                nameof(ProfileId) => ProfileId.FromValue(guid).ToString(),
                nameof(UpstreamId) => UpstreamId.FromValue(guid).ToString(),
                nameof(RateLimitPolicyId) => RateLimitPolicyId.FromValue(guid).ToString(),
                nameof(AccessListId) => AccessListId.FromValue(guid).ToString(),
                nameof(RuleSetId) => RuleSetId.FromValue(guid).ToString(),
                nameof(AuthProviderId) => AuthProviderId.FromValue(guid).ToString(),
                nameof(HostId) => HostId.FromValue(guid).ToString(),
                nameof(RouteId) => RouteId.FromValue(guid).ToString(),
                nameof(DestinationId) => DestinationId.FromValue(guid).ToString(),
                _ => throw new InvalidOperationException($"no ID source for {type.Name}"),
            };
        };
    }

    private static Guid Id(int n) => new($"01900000-0000-7000-8000-{n:x12}");

    // What a merge lands on: one of each kind of object a name can point at, and a host with a route.
    private static ConfigDocument StoredConfig() => new(
        Profiles: [new ProfileConfig(ProfileId.FromValue(Id(1)), "media", Settings: new HostSettings { ForceHttps = false })],
        Upstreams: [new UpstreamConfig(UpstreamId.FromValue(Id(1)), "jellyfin", Destinations: [new DestinationConfig(DestinationId.FromValue(Id(1)), "http://127.0.0.1:8096")])],
        Hosts:
        [
            new HostConfig(
                HostId.FromValue(Id(1)),
                ["jellyfin.example.com", "tv.example.com"],
                ProfileId: ProfileId.FromValue(Id(1)),
                Routes: [new RouteConfig(RouteId.FromValue(Id(1)), UpstreamId.FromValue(Id(1)))]),
        ],
        RateLimitPolicies: [new RateLimitPolicyConfig(RateLimitPolicyId.FromValue(Id(1)), "api")],
        AccessLists: [new AccessListConfig(AccessListId.FromValue(Id(1)), "staff", Ip: new AccessListIp(Allow: ["10.0.0.0/8"]))]);

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

        yield return ("empty-config", Text(Header));
        yield return ("respond-host", Text(Header + "hosts:\n  - names: [soon.example.com]\n    kind: respond\n    respond: {statusCode: 503, body: soon}\n"));
        yield return ("proxy-by-name", Text(Header + "profiles:\n  - name: media\nupstreams:\n  - name: jellyfin\n    destinations:\n      - address: http://127.0.0.1:8096\nhosts:\n  - names: [jellyfin.example.com]\n    profile: media\n    routes:\n      - upstream: jellyfin\n"));
        yield return ("matches-stored", Text(Header + "profiles:\n  - name: MEDIA\nhosts:\n  - names: [tv.example.com]\n    profile: media\n    routes:\n      - upstream: jellyfin\n        path: /web\n"));
        yield return ("settings", Text(Header + "settings:\n  unknownHost: close\n  defaults:\n    hsts: {enabled: true, maxAge: 31536000}\n    errorPages:\n      \"404\": {mode: message, heading: Gone}\n  trustedProxies: {mode: generic, proxies: [10.0.0.0/8]}\n"));
        yield return ("access-list", Text(Header + "accessLists:\n  - name: office\n    ip: {allow: [10.0.0.0/8], countries: {allow: [NO, ID]}}\n    basicAuth:\n      users:\n        - username: alice\n          credentialId: pwc_02wndAAHBC95f4Al8rNFlB\n"));
        yield return ("rule-set-and-rate-limit", Text(Header + "ruleSets:\n  - name: scanners\n    rules:\n      - action: deny\n        match: {userAgent: {contains: [sqlmap]}}\nrateLimitPolicies:\n  - name: strict\n    requests: 10\n    per: second\n"));
        yield return ("ids-given", Text(Header + "hosts:\n  - id: host_02wndAAHBC95f4Al8rNFlB\n    names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n  - id: host_02wndAAHBC95f4Al8rNFlB\n    names: [b.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n"));
        yield return ("flow-style", Text("{apiVersion: paduraksa.dev/v1, kind: Config, hosts: [{names: [a.example.com], kind: respond, respond: {statusCode: 200}}]}\n"));
        yield return ("unknown-key", Text(Header + "hosts:\n  - names: [a.example.com]\n    tag: [x]\n    version: 3\n    password: x\n"));
        yield return ("wrong-types", Text(Header + "hosts:\n  - names: a.example.com\n    enabled: yes\n    kind: [proxy]\n"));
        yield return ("tag", Text(Header + "hosts: !!python/object:os.system [id]\n"));
        yield return ("two-documents", Text(Header + "---\n" + Header));
        yield return ("alias-and-anchor", Text(Header + "profiles:\n  - &p\n    name: media\n    settings: {forceHttps: true}\nupstreams: &u\n  - name: x\n"));
        yield return ("alias-bomb", Text("a: &a [x, x, x, x, x, x, x, x, x]\nb: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a]\nc: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b]\nd: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c]\ne: &e [*d, *d, *d, *d, *d, *d, *d, *d, *d]\nf: &f [*e, *e, *e, *e, *e, *e, *e, *e, *e]\ng: &g [*f, *f, *f, *f, *f, *f, *f, *f, *f]\nh: [*g, *g, *g, *g, *g, *g, *g, *g, *g]\n"));
        yield return ("deep-nesting", Text(new string('[', 60) + new string(']', 60)));
        yield return ("merge-key", Text(Header + "profiles:\n  - &base {name: a}\n  - <<: *base\n"));
        yield return ("duplicate-key", Text(Header + "hosts: []\nhosts: []\n"));
        yield return ("block-scalars", Text(Header + "hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond:\n      statusCode: 404\n      body: |\n        <p>not here</p>\n        <p>sorry</p>\n      contentType: text/html\n"));
        yield return ("unicode", Text(Header + "profiles:\n  - name: \"caf\\u00e9 \\u65e5\\u672c\"\n    notes: \"tab\\t line\\nbreak\"\n"));
        yield return ("bom", [0xEF, 0xBB, 0xBF, .. Text(Header)]);
        yield return ("not-utf8", [0xFF, 0xFE, (byte)'a', (byte)':', (byte)' ', 0xC3]);
        yield return ("exported-sample", Text(ConfigYamlExporter.Export(CanonicalConfigJson.Canonicalize(StoredConfig()))));
        yield return ("unterminated-flow", Text("a: [1, 2\nb: 3\n"));
    }
}
