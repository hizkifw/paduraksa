using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Json;
using Paduraksa.Application.Logging;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Logging;

using static Paduraksa.Tests.Integration.Admin.AdminApiHarness;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The log files over the API (PLAN §7.4, §11.1): the list, a bounded tail, the download, the permission, and the names that are refused
/// before the file system is asked anything.
/// </summary>
public sealed class LogApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheListNamesEachFileWithItsLogSizeAndCompression()
    {
        await using AdminApiHarness api = await StartAsync();
        // Nothing in this test makes a public request, so the access log's writer (fed by the access-log drain) adds no line
        // while the test reads the file it wrote itself.
        Create(api, "access.log", "GET / 200\n");
        CreateGzip(api, LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Access, DateTimeOffset.UtcNow.AddDays(-1))), "GET /two 404\n");

        using HttpResponseMessage response = await api.GetAsync("/api/v1/logs");
        IReadOnlyList<LogFileSummary> files = await ReadAsync(response, AdminJsonContext.Default.IReadOnlyListLogFileSummary, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LogFileSummary live = Assert.Single(files, file => file.Name == "access.log");
        Assert.Equal("access", live.Log);
        Assert.Equal(10, live.Size);
        Assert.False(live.Compressed);
        Assert.Contains(files, file => file.Compressed && file.Name.EndsWith(".log.gz", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task ReadingTheLogsNeedsTheReadPermission(string? caller, HttpStatusCode expected)
    {
        await using AdminApiHarness api = await StartAsync();
        Create(api, "error.log", "warn: one\n");

        using HttpResponseMessage list = await api.GetAsync("/api/v1/logs", caller);
        using HttpResponseMessage tail = await api.GetAsync("/api/v1/logs/error.log/tail", caller);
        using HttpResponseMessage download = await api.GetAsync("/api/v1/logs/error.log", caller);

        Assert.Equal(expected, list.StatusCode);
        Assert.Equal(expected, tail.StatusCode);
        Assert.Equal(expected, download.StatusCode);
    }

    [Fact]
    public async Task ATailIsBoundedByLinesAndCanBeFiltered()
    {
        await using AdminApiHarness api = await StartAsync();
        Create(api, "access.log", string.Concat(Enumerable.Range(0, 1500).Select(i => $"GET /{i} 200\n")));

        using HttpResponseMessage all = await api.GetAsync("/api/v1/logs/access.log/tail?lines=1000");
        LogFileTail tail = await ReadAsync(all, AdminJsonContext.Default.LogFileTail, Ct);
        using HttpResponseMessage filtered = await api.GetAsync("/api/v1/logs/access.log/tail?lines=5&filter=%2F1499");
        LogFileTail matching = await ReadAsync(filtered, AdminJsonContext.Default.LogFileTail, Ct);

        Assert.Equal(1000, tail.Lines.Count);
        Assert.Equal("GET /1499 200", tail.Lines[^1]);
        Assert.True(tail.Truncated);
        Assert.Equal(["GET /1499 200"], matching.Lines);
    }

    [Theory]
    [InlineData("/api/v1/logs/error.log/tail?lines=0")]
    [InlineData("/api/v1/logs/error.log/tail?lines=5000")]
    [InlineData("/api/v1/logs/error.log/tail?lines=many")]
    [InlineData("/api/v1/logs/error.log/tail?filter=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ABadTailParameterIsAProblem(string path)
    {
        await using AdminApiHarness api = await StartAsync();
        Create(api, "error.log", "warn: one\n");

        using HttpResponseMessage response = await api.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ProblemAsync(response, Ct)).Status);
    }

    [Theory]
    [InlineData("no-such-file.log")]
    [InlineData("error-2026.log")]
    [InlineData("notes.txt")]
    [InlineData("%2e%2e%2fpaduraksa.db")]
    [InlineData("..%2Fpaduraksa.db")]
    [InlineData("%2Fetc%2Fpasswd")]
    public async Task OnlyTheWritersOwnNamesAreServed(string name)
    {
        await using AdminApiHarness api = await StartAsync();
        Create(api, "error.log", "warn: one\n");
        await System.IO.File.WriteAllTextAsync(Path.Combine(DirectoryOf(api), "notes.txt"), "not a log", Ct);

        using HttpResponseMessage tail = await api.GetAsync("/api/v1/logs/" + name + "/tail");
        using HttpResponseMessage download = await api.GetAsync("/api/v1/logs/" + name);

        Assert.Equal(HttpStatusCode.NotFound, tail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task ASymbolicLinkIsNotFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using AdminApiHarness api = await StartAsync();
        string outside = Path.Combine(api.Host.Options.DataDirectory, "outside.txt");
        await System.IO.File.WriteAllTextAsync(outside, "warn: from outside\n", Ct);
        string link = Path.Combine(DirectoryOf(api), "error.log");
        System.IO.File.Delete(link);
        System.IO.File.CreateSymbolicLink(link, outside);

        using HttpResponseMessage tail = await api.GetAsync("/api/v1/logs/error.log/tail");
        using HttpResponseMessage download = await api.GetAsync("/api/v1/logs/error.log");

        Assert.Equal(HttpStatusCode.NotFound, tail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task ADownloadIsAnAttachmentOfTheFileItself()
    {
        await using AdminApiHarness api = await StartAsync();
        string rotated = LogFileNames.Rotated(LogFileKind.Access, new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        Create(api, rotated, "GET / 200\nGET /two 404\n");
        CreateGzip(api, LogFileNames.Compressed(rotated), "GET / 200\n");

        using HttpResponseMessage plain = await api.GetAsync("/api/v1/logs/" + rotated);
        string body = await plain.Content.ReadAsStringAsync(Ct);
        using HttpResponseMessage compressed = await api.GetAsync("/api/v1/logs/" + LogFileNames.Compressed(rotated));

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("GET / 200\nGET /two 404\n", body);
        Assert.Equal("text/plain", plain.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", plain.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains(rotated, plain.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Equal("application/gzip", compressed.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnreadableLogsDirectoryIsA500ProblemNotAnEmptyList()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(
            Ct,
            adminServices: services => services.AddSingleton<ILogFileStore>(new UnreadableLogStore()));

        using HttpResponseMessage response = await api.GetAsync("/api/v1/logs");
        ApiProblem problem = await ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(500, problem.Status);
        Assert.Contains("couldn't be read", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheApiDescriptionListsTheLogEndpointsWithTheirPermission()
    {
        await using AdminApiHarness api = await StartAsync();

        using HttpResponseMessage response = await api.GetAsync("/api/v1/openapi.json");
        string document = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("/api/v1/logs", document, StringComparison.Ordinal);
        Assert.Contains("logs:read", document, StringComparison.Ordinal);
    }

    private static async Task<AdminApiHarness> StartAsync()
    {
        AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        System.IO.Directory.CreateDirectory(DirectoryOf(api));
        return api;
    }

    private static string DirectoryOf(AdminApiHarness api) => api.Host.Options.Logs.ResolvedDirectory;

    private static void Create(AdminApiHarness api, string name, string text) =>
        System.IO.File.WriteAllText(Path.Combine(DirectoryOf(api), name), text);

    private static void CreateGzip(AdminApiHarness api, string name, string text)
    {
        using FileStream file = System.IO.File.Create(Path.Combine(DirectoryOf(api), name));
        using GZipStream gzip = new(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A store whose directory can't be read: the API must answer a problem, not an empty list that reads as "no logs".</summary>
    private sealed class UnreadableLogStore : ILogFileStore
    {
        public string Directory => "/unreadable/logs";

        public IReadOnlyList<LogFileSummary> List() => throw new IOException("the directory is unreadable");

        public LogFileTail? ReadTail(string name, int maxLines, string? filter = null) => null;

        public Stream? OpenRead(string name, out long length)
        {
            length = 0;
            return null;
        }
    }
}
