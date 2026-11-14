using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Logging;

public sealed class AccessLogFormatterTests
{
    private static readonly AccessLogFormatOptions Defaults = AccessLogFormatOptions.Default;

    [Fact]
    public void AFullLineHasTheDocumentedFieldsInOrder()
    {
        var entry = Complete();

        var line = Format(entry);

        Assert.Equal(
            "2026-01-31T12:34:56.789Z 198.51.100.7 \"app.example:8443\" \"GET /a/b?x=1&token=REDACTED HTTP/2\" 200 512 1.234 0.987 "
            + "\"http://10.0.0.2:8080/\" \"0k3m9x2p-00000000000ab\" \"8f2a1c3d4e5f6789\" \"https://example.com/\" \"Mozilla/5.0\" "
            + "2.101 1 0.000 0.012 0.000 0.105 0.987",
            line);
        // The sink owns the newline.
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingValuesAreABareDash()
    {
        AccessLogEntry entry = new() { TimestampUtcTicks = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc).Ticks, StatusCode = 403, TotalTicks = 0 };

        var line = Format(entry);

        Assert.Equal("2026-01-31T00:00:00.000Z - - \"- - -\" 403 - 0.000 - - - - - - - - - - - - -", line);
    }

    [Fact]
    public void AnAbortedRequestWritesNoStatus()
    {
        // The gate dropped the request: nothing was sent, so the status is '-', not the 200 an unsent response reports.
        var entry = Target("/drop") with { StatusCode = AccessLogEntry.NoResponse };

        Assert.Equal("0001-01-01T00:00:00.000Z - - \"GET /drop HTTP/1.1\" - - 0.000 - - - - - - - - - - - - -", Format(entry));
    }

    [Fact]
    public void TimeIsUtcWithMillisecondsAndDurationsAreMillisecondsWithThreeDecimals()
    {
        AccessLogEntry entry = new()
        {
            TimestampUtcTicks = new DateTime(2026, 2, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(8_000).Ticks,
            TotalTicks = TimeSpan.FromMicroseconds(1_234.5).Ticks,
            UpstreamTicks = TimeSpan.FromSeconds(2.5).Ticks,
        };

        var line = Format(entry);

        Assert.StartsWith("2026-02-03T04:05:06.007Z ", line, StringComparison.Ordinal);
        // No status and no byte count, then the durations: 1.235 ms (rounded from microseconds) and 2500.000 ms.
        Assert.Contains(" - - 1.235 2500.000 ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStagesFollowTheOlderFieldsAndAReusedHandshakeIsADash()
    {
        var entry = Target("/again") with
        {
            StatusCode = 200,
            TlsHandshakeTicks = AccessLogEntry.HandshakeReused,
            ConnectionRequest = 7,
            RequestBodyTicks = TimeSpan.FromMilliseconds(250).Ticks,
            GateTicks = TimeSpan.FromMicroseconds(3).Ticks,
            UpstreamConnectTicks = TimeSpan.FromMilliseconds(40.5).Ticks,
            ResponseSendTicks = TimeSpan.FromSeconds(1.25).Ticks,
        };

        Assert.EndsWith(" - - - - - - 7 250.000 0.003 40.500 1250.000 -", Format(entry), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"", "\\\"")]
    [InlineData("\\", "\\\\")]
    [InlineData("\r", "\\x0d")]
    [InlineData("\n", "\\x0a")]
    [InlineData("\t", "\\x09")]
    [InlineData("\0", "\\x00")]
    [InlineData("\x7f", "\\x7f")]
    [InlineData("\u0085", "\\x85")]
    [InlineData("\u0080", "\\x80")]
    [InlineData("\u009f", "\\x9f")]
    [InlineData("\u2028", "\\u2028")]
    [InlineData("\u2029", "\\u2029")]
    [InlineData("café ☕", "café ☕")]
    public void HostileAndUnusualCharactersCannotBreakTheLine(string input, string expected)
    {
        var entry = Target("/x") with { UserAgent = input, RequestId = "r" };

        var line = Format(entry);

        Assert.Contains($"\"{expected}\"", line, StringComparison.Ordinal);
        Assert.Single(line.Split('\n'));
    }

    [Fact]
    public void ACraftedUserAgentCannotForgeALine()
    {
        var entry = Target("/login") with
        {
            StatusCode = 200,
            UserAgent = "evil\" 200 0 0.000 - - - - -\r\n2026-01-31T00:00:00.000Z 1.2.3.4 \"x\" \"GET /admin\" 200 0",
        };

        var line = Format(entry);

        Assert.Single(line.Split('\n'));
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        // The line break arrives escaped, inside the quoted user agent: it can't end the line or start one.
        Assert.Contains("\\x0d\\x0a2026-01-31T00:00:00.000Z", line, StringComparison.Ordinal);
        Assert.Contains("\"GET /login ", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("TOKEN")]
    [InlineData("api_key")]
    [InlineData("ApiKey")]
    [InlineData("signature")]
    [InlineData("session")]
    [InlineData("password")]
    public void TheDefaultSensitiveNamesAreRedactedWhateverTheirCase(string name)
    {
        var entry = Target("/a", $"?{name}=s3cret");

        var line = Format(entry);

        Assert.Contains($"{name}=REDACTED", line, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", line, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheNamedParametersAreRedacted()
    {
        var entry = Target("/search", "?q=proxy&token=abc&page=2&access_token=def");

        var line = Format(entry);

        Assert.Contains("?q=proxy&token=REDACTED&page=2&access_token=REDACTED", line, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", line, StringComparison.Ordinal);
        Assert.DoesNotContain("def", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?token", "?token=REDACTED")]
    [InlineData("?token=", "?token=REDACTED")]
    [InlineData("?a=1&token", "?a=1&token=REDACTED")]
    [InlineData("?&&token=1", "?&&token=REDACTED")] // empty segments are kept as written
    [InlineData("?a=1&&b=2", "?a=1&&b=2")]
    [InlineData("?=x", "?=x")] // no name, nothing sensitive
    [InlineData("?a", "?a")]
    [InlineData("?", "?")]
    [InlineData("?tokenx=1", "?tokenx=1")] // a longer name that merely starts alike
    [InlineData("?%74oken=1", "?%74oken=1")] // names are compared as written, not percent-decoded
    public void OddQueriesAreHandledAsWritten(string query, string expected)
    {
        var entry = Target("/a", query);

        Assert.Contains($"\"GET /a{expected} HTTP/1.1\"", Format(entry), StringComparison.Ordinal);
    }

    [Fact]
    public void TheQueryStringCanBeLeftOutEntirely()
    {
        var entry = Target("/a", "?q=1&token=abc");

        Assert.DoesNotContain("token", Format(entry, Defaults with { LogQueryString = false }), StringComparison.Ordinal);
        Assert.Contains("\"GET /a HTTP/1.1\"", Format(entry, Defaults with { LogQueryString = false }), StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredListReplacesTheBuiltInOne()
    {
        var options = Defaults with { SensitiveQueryKeys = ["pin"] };
        var entry = Target("/a", "?pin=1234&token=abc");

        var line = Format(entry, options);

        Assert.Contains("?pin=REDACTED&token=abc", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LongValuesAreCutAtTheirCapAndMarked()
    {
        var entry = Target("/" + new string('a', AccessLogFormatter.MaxPathLength * 2)) with
        {
            UserAgent = new string('b', AccessLogFormatter.MaxHeaderValueLength * 2),
            Query = "?q=" + new string('c', AccessLogFormatter.MaxQueryLength * 2),
        };

        var line = Format(entry);

        Assert.True(line.Length <= AccessLogFormatter.MaxLineLength, $"the line is {line.Length} characters");
        Assert.Contains(AccessLogFormatter.TruncationMarker, line, StringComparison.Ordinal);
        // No field can exceed its cap, marker included, so one pathological request can't blow up a line.
        Assert.DoesNotContain(new string('b', AccessLogFormatter.MaxHeaderValueLength + 1), line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', AccessLogFormatter.MaxQueryLength + 1), line, StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongPathIsMarkedAsCut()
    {
        var entry = Target("/" + new string('a', AccessLogFormatter.MaxPathLength * 2));

        var line = Format(entry);

        Assert.Contains(AccessLogFormatter.TruncationMarker, line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', AccessLogFormatter.MaxPathLength + 1), line, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorstCaseEntryStillFitsTheLineBuffer()
    {
        // Every field at its cap, and as many characters that escape to four as possible.
        var worst = string.Concat(Enumerable.Repeat("\x01\"\\", 2_000));
        AccessLogEntry entry = new()
        {
            ClientAddress = worst,
            Host = worst,
            Method = worst,
            Path = worst,
            Query = "?" + worst,
            Protocol = worst,
            UpstreamAddress = worst,
            RequestId = worst,
            CloudflareRay = worst,
            Referer = worst,
            UserAgent = worst,
            ResponseBytes = long.MaxValue,
            TotalTicks = TimeSpan.MaxValue.Ticks,
            UpstreamTicks = TimeSpan.MaxValue.Ticks,
            TlsHandshakeTicks = long.MaxValue,
            ForwardStartMicros = 0,
        };

        var buffer = new char[AccessLogFormatter.MaxLineLength];
        var length = AccessLogFormatter.Format(entry, Defaults, buffer);

        Assert.InRange(length, 1, AccessLogFormatter.MaxLineLength);
        Assert.Equal(length, Format(entry).Length);
    }

    [Fact]
    public void ABufferTooSmallIsReportedRatherThanTruncated()
    {
        Assert.Equal(-1, AccessLogFormatter.Format(Complete(), Defaults, new char[8]));
    }

    [Fact]
    public void ALineWithAQueryAllocatesNothing()
    {
        // The sensitive-name check used to enumerate its list through the interface, which boxes an enumerator for every parameter.
        var entry = Target("/a/b", "?x=1&token=abc&y=2&code&=&&sig=zz") with { Host = "app.example", Protocol = "HTTP/2", StatusCode = 200 };
        var buffer = new char[AccessLogFormatter.MaxLineLength];
        AccessLogFormatOptions custom = Defaults with { SensitiveQueryKeys = ["alpha", "x-token", "token"] };

        AllocAssert.Zero((entry, buffer), static s => AccessLogFormatter.Format(s.entry, Defaults, s.buffer), "a line with a query and the default sensitive keys");
        AllocAssert.Zero((entry, buffer, custom), static s => AccessLogFormatter.Format(s.entry, s.custom, s.buffer), "a line with a query and custom sensitive keys");
    }

    [Fact]
    public void ANonAsciiPathStaysReadable()
    {
        var entry = Target("/café/日本語", "?q=☕");

        Assert.Contains("GET /café/日本語?q=☕ HTTP/1.1", Format(entry), StringComparison.Ordinal);
    }

    private static AccessLogEntry Target(string path, string? query = null) => new()
    {
        Method = "GET",
        Path = path,
        Query = query,
        Protocol = "HTTP/1.1",
    };

    private static AccessLogEntry Complete() => new()
    {
        TimestampUtcTicks = new DateTime(2026, 1, 31, 12, 34, 56, 789, DateTimeKind.Utc).Ticks,
        ClientAddress = "198.51.100.7",
        Host = "app.example:8443",
        Method = "GET",
        Path = "/a/b",
        Query = "?x=1&token=secret-value",
        Protocol = "HTTP/2",
        StatusCode = 200,
        ResponseBytes = 512,
        TotalTicks = TimeSpan.FromMicroseconds(1_234).Ticks,
        UpstreamTicks = TimeSpan.FromMicroseconds(987).Ticks,
        UpstreamAddress = "http://10.0.0.2:8080/",
        RequestId = "0k3m9x2p-00000000000ab",
        CloudflareRay = "8f2a1c3d4e5f6789",
        Referer = "https://example.com/",
        UserAgent = "Mozilla/5.0",
        TlsHandshakeTicks = TimeSpan.FromMicroseconds(2_101).Ticks,
        ConnectionRequest = 1,
        RequestBodyTicks = 0,
        GateTicks = TimeSpan.FromMicroseconds(12).Ticks,
        UpstreamConnectTicks = 0,
        ResponseSendTicks = TimeSpan.FromMicroseconds(105).Ticks,
        ForwardStartMicros = 20,
    };

    private static string Format(AccessLogEntry entry, AccessLogFormatOptions? options = null)
    {
        var buffer = new char[AccessLogFormatter.MaxLineLength];
        var length = AccessLogFormatter.Format(entry, options ?? Defaults, buffer);
        Assert.True(length > 0);
        return new string(buffer, 0, length);
    }
}
