using System.Diagnostics;
using System.Text.Json.Nodes;

using Paduraksa.Application.Yaml;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// The setup the way an operator does it: the first-run setup with the token from the data directory, an API token for the run, and the whole config as one YAML import
/// (replace mode). Then the config's objects are read back, so the changes can edit them by ID with the version each carries.
/// </summary>
internal sealed class Control(SoakOptions options, ServerProcess server, SoakConfig config, Events events)
{
    private const string Owner = "soak";
    private const string OwnerPassword = "Soak-Password-0123456789-xyz";

    public AdminClient Admin { get; } = new(options.Admin);

    public string Token => Admin.Token ?? throw new InvalidOperationException("Not set up.");

    public static string OwnerName => Owner;

    public static string OwnerSecret => OwnerPassword;

    public async Task WaitReadyAsync(TimeSpan limit, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using HttpClient probe = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = static (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(3) };
        while (watch.Elapsed < limit)
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException("The server exited while starting; see server.log.");
            }

            try
            {
                using var ready = await probe.GetAsync($"https://127.0.0.1:{options.Admin}/readyz", ct);
                using var pub = await probe.GetAsync($"http://127.0.0.1:{options.PublicHttp}/", ct);
                if (ready.IsSuccessStatusCode && (int)pub.StatusCode is 404 or 200)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException($"The server was not ready within {limit.TotalSeconds:F0} s.");
    }

    public async Task SetUpAsync(CancellationToken ct)
    {
        var setupToken = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-token"), ct)).Trim();
        var setup = await Admin.PostAsync("/api/v1/setup", $$"""{"setupToken":"{{setupToken}}","username":"{{Owner}}","password":"{{OwnerPassword}}","displayName":"Soak"}""", ct: ct);
        Require(setup, "first-run setup");
        var cookie = setup.SessionCookie ?? throw new InvalidOperationException("Setup gave no session cookie.");
        var issued = await Admin.SendAsync(
            HttpMethod.Post, "/api/v1/tokens",
            """{"name":"soak","scopes":["config:read","config:write","metrics:read","logs:read","audit:read","simulate","providers:read","providers:write"]}""",
            "application/json", ct, cookie: cookie);
        Require(issued, "issuing the API token");
        Admin.Token = issued.Json!["token"]!.GetValue<string>();
    }

    public async Task ImportConfigAsync(CancellationToken ct)
    {
        var yaml = ConfigYamlExporter.Export(config.Document);
        await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "config.yaml"), yaml, ct);
        var result = await Admin.PostAsync("/api/v1/config/import?mode=replace&message=soak", yaml, "application/yaml", ct);
        Require(result, "importing the config");
        events.Write("config.imported", new { hosts = config.AllHostNames.Count, ms = Math.Round(result.Milliseconds) });
    }

    public static void Require(AdminResult result, string what)
    {
        if (!result.Ok)
        {
            throw new InvalidOperationException($"{what} failed: HTTP {(int)result.Status}: {result.Body}");
        }
    }
}

/// <summary>A config object as the API gives it, with the version its next change has to quote.</summary>
internal sealed class Resource(string path, JsonObject body, long version)
{
    public string Path { get; } = path;

    public JsonObject Body { get; set; } = body;

    public long Version { get; set; } = version;
}

/// <summary>The objects the changes edit, found by name and kept current: every successful PUT hands back the new version.</summary>
internal sealed class ResourceStore(AdminClient admin)
{
    private readonly Dictionary<string, Resource> _byName = new(StringComparer.Ordinal);

    public async Task LoadAsync(string collection, string nameMember, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var page = await admin.GetAsync($"/api/v1/{collection}?limit=200" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), ct);
            Control.Require(page, "listing " + collection);
            var json = page.Json!;
            foreach (var item in json["items"]!.AsArray())
            {
                var body = item!.AsObject();
                var name = nameMember == "names" ? body["names"]![0]!.GetValue<string>() : body[nameMember]!.GetValue<string>();
                _byName[collection + ":" + name] = new Resource($"/api/v1/{collection}/{body["id"]!.GetValue<string>()}", body, body["version"]!.GetValue<long>());
            }

            cursor = json["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);
    }

    public Resource Get(string collection, string name) => _byName[collection + ":" + name];

    /// <summary>
    /// Replaces the object; returns the call's result. A change answers with the command's outcome (revision, publish, the object's new <c>version</c>), not the object, so the
    /// stored copy keeps the body that was sent and takes the version; a failure reads the object again, so the next edit starts from what the server holds.
    /// </summary>
    public async Task<AdminResult> PutAsync(Resource resource, CancellationToken ct)
    {
        var result = await admin.PutAsync(resource.Path + "?message=soak", resource.Body.ToJsonString(), resource.Version, ct);
        if (result.Ok && result.Json?["version"] is { } version)
        {
            resource.Version = version.GetValue<long>();
            return result;
        }

        var fresh = await admin.GetAsync(resource.Path, ct);
        if (fresh.Ok && fresh.Json is JsonObject body)
        {
            resource.Body = body;
            resource.Version = body["version"]!.GetValue<long>();
        }

        return result;
    }
}
