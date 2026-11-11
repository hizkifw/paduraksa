using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>The error pages and maintenance mode at every layer that sets them, and the references they keep.</summary>
public sealed class ErrorPagesValidationTests
{
    private const string Proxy = "/hosts/0";

    [Fact]
    public void AMessageAndADocumentAreValidAtEveryLayer()
    {
        HostSettings valid = new()
        {
            ErrorPages = new ErrorPagesSettings
            {
                NotFound = new ErrorPageContent { Heading = "{status} {statusText}", Text = "Nothing at {host}. Quote {requestId} ({cfRay})." },
                ServiceUnavailable = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<style>p{color:red}</style><p>{status} for {host}</p>" },
                Maintenance = new ErrorPageContent { Heading = "Back soon", Text = string.Empty },
                ReplaceUpstream = UpstreamErrorReplacement.Html,
            },
        };
        var document = Valid();
        Assert.Empty(ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = valid } }));
        Assert.Empty(ConfigValidator.Validate(document with { Profiles = [document.Profiles[0] with { Settings = valid }] }));
        Assert.Empty(ConfigValidator.Validate(document with { Hosts = Host(document, valid) }));
    }

    [Fact]
    public void AMessageNeedsAHeadingAndRefusesADocument()
    {
        AssertErrors(
            OnHost(new ErrorPagesSettings { Forbidden = new ErrorPageContent { Text = "no" } }),
            ($"{Proxy}/settings/errorPages/403/heading", ErrorPagesHeadingRequired));
        AssertErrors(
            OnHost(new ErrorPagesSettings { Forbidden = new ErrorPageContent { Heading = " ", Html = "<p>x</p>" } }),
            ($"{Proxy}/settings/errorPages/403/html", ErrorPagesHtmlUnexpected),
            ($"{Proxy}/settings/errorPages/403/heading", ErrorPagesHeadingRequired));
    }

    [Fact]
    public void ADocumentNeedsItselfAndRefusesAMessage()
    {
        AssertErrors(
            OnHost(new ErrorPagesSettings { BadGateway = new ErrorPageContent { Mode = ErrorPageMode.Html } }),
            ($"{Proxy}/settings/errorPages/502/html", ErrorPagesHtmlRequired));
        AssertErrors(
            OnHost(new ErrorPagesSettings { BadGateway = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<p>x</p>", Heading = "h", Text = "t" } }),
            ($"{Proxy}/settings/errorPages/502/heading", ErrorPagesHeadingUnexpected),
            ($"{Proxy}/settings/errorPages/502/text", ErrorPagesTextUnexpected));
    }

    [Fact]
    public void TextsAreBoundedAndPlain()
    {
        AssertErrors(
            OnHost(new ErrorPagesSettings { NotFound = new ErrorPageContent { Heading = new string('a', ErrorPageText.MaxHeadingLength + 1), Text = new string('a', ErrorPageText.MaxTextLength + 1) } }),
            ($"{Proxy}/settings/errorPages/404/heading", ErrorPagesHeadingTooLong),
            ($"{Proxy}/settings/errorPages/404/text", ErrorPagesTextTooLong));
        AssertErrors(
            OnHost(new ErrorPagesSettings { NotFound = new ErrorPageContent { Heading = "two\nlines", Text = "tab\there" } }),
            ($"{Proxy}/settings/errorPages/404/heading", ErrorPagesHeadingInvalid),
            ($"{Proxy}/settings/errorPages/404/text", ErrorPagesTextInvalid));
        AssertErrors(
            OnHost(new ErrorPagesSettings { Maintenance = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = new string('a', ErrorPageText.MaxHtmlBytes + 1) } }),
            ($"{Proxy}/settings/errorPages/maintenance/html", ErrorPagesHtmlTooLong));

        // The cap is in UTF-8 bytes, so a document of two-byte characters is over it at half the length.
        AssertErrors(
            OnHost(new ErrorPagesSettings { Maintenance = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = new string('é', ErrorPageText.MaxHtmlBytes / 2 + 1) } }),
            ($"{Proxy}/settings/errorPages/maintenance/html", ErrorPagesHtmlTooLong));
        AssertErrors(
            OnHost(new ErrorPagesSettings { Maintenance = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<p>a\u0000b</p>" } }),
            ($"{Proxy}/settings/errorPages/maintenance/html", ErrorPagesHtmlInvalid));
        AssertErrors(
            OnHost(new ErrorPagesSettings { Maintenance = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<p>a" + new string('\ud800', 1) + "b</p>" } }),
            ($"{Proxy}/settings/errorPages/maintenance/html", ErrorPagesHtmlInvalid));
    }

    [Fact]
    public void AnUnknownVariableIsRefusedAndTheMessageNamesTheAllowedOnes()
    {
        var errors = ConfigValidator.Validate(OnHost(new ErrorPagesSettings
        {
            NotFound = new ErrorPageContent { Heading = "{state}", Text = "{status} and {nope}, {{literal} {status:x}" },
        }));
        Assert.Equal(
            [($"{Proxy}/settings/errorPages/404/heading", ErrorPagesVariableUnknown), ($"{Proxy}/settings/errorPages/404/text", ErrorPagesVariableUnknown)],
            errors.Select(e => (e.Path, e.Code)));
        Assert.All(errors, error => Assert.Contains("{status}, {statusText}, {requestId}, {host}, {cfRay}", error.Message, StringComparison.Ordinal));
        Assert.Contains("{state}", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("{nope}", errors[1].Message, StringComparison.Ordinal);

        // The braces of a stylesheet aren't variables, so a document needs no escaping for its CSS.
        Assert.Empty(ConfigValidator.Validate(OnHost(new ErrorPagesSettings
        {
            ServiceUnavailable = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<style>@media print{body{margin:0}} a{}</style>{{status}" },
        })));
    }

    [Fact]
    public void AnUndefinedModeOrReplacementIsTheEnumsError()
    {
        AssertErrors(
            OnHost(new ErrorPagesSettings { Forbidden = new ErrorPageContent { Mode = (ErrorPageMode)9 }, ReplaceUpstream = (UpstreamErrorReplacement)9 }),
            ($"{Proxy}/settings/errorPages/replaceUpstream", EnumUndefined),
            ($"{Proxy}/settings/errorPages/403/mode", EnumUndefined));
    }

    [Fact]
    public void AnErrorAtAProfileOrTheDefaultsNamesThatLayer()
    {
        var document = Valid();
        HostSettings bad = new() { ErrorPages = new ErrorPagesSettings { NotFound = new ErrorPageContent() } };
        AssertErrors(
            document with { Settings = document.Settings with { Defaults = bad } },
            ("/settings/defaults/errorPages/404/heading", ErrorPagesHeadingRequired));
        AssertErrors(
            document with { Profiles = [document.Profiles[0] with { Settings = bad }] },
            ("/profiles/0/settings/errorPages/404/heading", ErrorPagesHeadingRequired));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(503)]
    [InlineData(410)]
    public void AMaintenanceStatusFromTheListIsValid(int status) =>
        Assert.Empty(ConfigValidator.Validate(OnHost(maintenance: new MaintenanceSettings { Enabled = true, Status = status, RetryAfterSeconds = 120 })));

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(0)]
    [InlineData(600)]
    public void AnyOtherMaintenanceStatusIsRefused(int status) =>
        AssertErrors(
            OnHost(maintenance: new MaintenanceSettings { Status = status }),
            ($"{Proxy}/settings/maintenance/status", MaintenanceStatusInvalid));

    [Theory]
    [InlineData(-1)]
    [InlineData(604801)]
    public void ARetryAfterOutsideZeroToAWeekIsRefused(int seconds) =>
        AssertErrors(
            OnHost(maintenance: new MaintenanceSettings { RetryAfterSeconds = seconds }),
            ($"{Proxy}/settings/maintenance/retryAfterSeconds", MaintenanceRetryAfterInvalid));

    [Fact]
    public void ABypassListMustExistWhereverTheLayerThatNamesItIs()
    {
        var document = Valid();
        var missing = Id<AccessListId>(99);
        AssertErrors(
            document with { Hosts = Host(document, maintenance: new MaintenanceSettings { BypassAccessListId = missing }) },
            ($"{Proxy}/settings/maintenance/bypassAccessListId", HostMaintenanceBypassUnknown));

        var errors = ConfigValidator.Validate(document with { Settings = document.Settings with { Defaults = new HostSettings { Maintenance = new MaintenanceSettings { BypassAccessListId = missing } } } });
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.Equal(HostMaintenanceBypassUnknown, error.Code));
        Assert.All(errors, error => Assert.Contains("inherited from the global defaults", error.Message, StringComparison.Ordinal));

        AssertErrors(
            document with { Hosts = Host(document, maintenance: new MaintenanceSettings { BypassAccessListId = default(AccessListId) }) },
            ($"{Proxy}/settings/maintenance/bypassAccessListId", IdEmpty));
    }

    private static ConfigDocument OnHost(ErrorPagesSettings? errorPages = null, MaintenanceSettings? maintenance = null)
    {
        var document = Valid();
        return document with { Hosts = Host(document, new HostSettings { ErrorPages = errorPages, Maintenance = maintenance }) };
    }

    private static HostConfig[] Host(ConfigDocument document, HostSettings? settings = null, MaintenanceSettings? maintenance = null) =>
        [.. document.Hosts.Select(h => h.Id == AppHost ? h with { Settings = settings ?? new HostSettings { Maintenance = maintenance } } : h)];

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));
}
