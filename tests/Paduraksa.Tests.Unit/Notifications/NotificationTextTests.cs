using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Tests.Unit.Notifications;

/// <summary>The words of each event, pinned: short, specific, and with something to do.</summary>
public sealed class NotificationTextTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 14, 2, 0, TimeSpan.Zero);
    private static readonly UpstreamId Upstream = UpstreamId.New();

    private static DestinationReading Reading(string address, bool down, string probe) => new(
        DestinationId.New(),
        address,
        down ? DestinationVerdict.Unhealthy : DestinationVerdict.Healthy,
        DestinationVerdict.Unknown,
        down ? DestinationVerdict.Unhealthy : DestinationVerdict.Healthy,
        new ProbeOutcome(At, !down, probe));

    private static HealthChange Change(HealthScope scope, bool down, params DestinationReading[] readings) => new(
        scope, Upstream, scope == HealthScope.Destination ? readings[0].Id : null, scope == HealthScope.Destination ? readings[0].Address : null, down, At, null, Flapping: false, Settled: false, readings);

    private static NotificationEvent Health(HealthChange change) => NotificationText.Health(change, "media", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

    [Fact]
    public void AnUpstreamDownNamesTheDestinationsTheirReasonsAndWhatToDo()
    {
        var e = Health(Change(HealthScope.Upstream, true, Reading("http://10.0.0.5:8096", true, "HTTP 503"), Reading("http://10.0.0.6:8096", true, "timed out")));

        Assert.Equal(NotificationEventKind.UpstreamDown, e.Kind);
        Assert.Equal("Upstream media is down", e.Title);
        Assert.Equal(
            "No destination takes traffic since 14:02 UTC: http://10.0.0.5:8096 (active probe: HTTP 503), http://10.0.0.6:8096 (active probe: timed out). Check the services behind it; it recovers by itself once a check passes.",
            e.Body);
        Assert.Equal((NotificationSeverity.Critical, "/upstreams"), (e.Severity, e.LinkPath));
        Assert.Contains(new KeyValuePair<string, string>("upstreamId", Upstream.ToString()), e.Data!);
    }

    [Fact]
    public void AnUpstreamBackUpSaysHowManyWorkAndWhichStillDoNot()
    {
        var e = Health(Change(HealthScope.Upstream, false, Reading("http://a:80", false, "HTTP 200"), Reading("http://b:80", true, "HTTP 500")));

        Assert.Equal("Upstream media is back up", e.Title);
        Assert.Equal("1 of 2 destinations take traffic again since 14:02 UTC. Still out: http://b:80 (active probe: HTTP 500).", e.Body);
        Assert.Equal(NotificationSeverity.Good, e.Severity);
    }

    [Fact]
    public void ADestinationDownIsAWarningThatSaysHowManyStillWork()
    {
        var down = Reading("http://b:80", true, "connection refused");
        var e = Health(Change(HealthScope.Destination, true, down, Reading("http://a:80", false, "HTTP 200")) with { Reason = "active probe: connection refused" });

        Assert.Equal((NotificationEventKind.DestinationDown, NotificationSeverity.Warning), (e.Kind, e.Severity));
        Assert.Equal("http://b:80 of upstream media is down", e.Title);
        Assert.Equal("active probe: connection refused since 14:02 UTC. 1 of 2 destinations still take traffic. Check the service on that address.", e.Body);
    }

    [Fact]
    public void FlappingAndSettledSayWhatWillAndWillNotBeReported()
    {
        var reading = Reading("http://a:80", true, "HTTP 503");
        var flapping = Health(Change(HealthScope.Upstream, true, reading) with { Flapping = true });
        var settled = Health(Change(HealthScope.Upstream, true, reading) with { Settled = true });

        Assert.Equal("Upstream media keeps changing state", flapping.Title);
        Assert.Contains("not reported again until it has been stable for 10 minutes", flapping.Body, StringComparison.Ordinal);
        Assert.Equal(NotificationEventKind.UpstreamDown, flapping.Kind);
        Assert.Equal("Upstream media has settled: down", settled.Title);
    }

    [Theory]
    [InlineData(CertificateSource.Acme, 29.5, "Certificate app.example.com expires in 30 days", "It is renewed automatically", NotificationSeverity.Info)]
    [InlineData(CertificateSource.Uploaded, 6.2, "Certificate app.example.com expires in 7 days", "Upload a new certificate before then.", NotificationSeverity.Warning)]
    [InlineData(CertificateSource.LocalCaRoot, 0.4, "Certificate app.example.com expires within a day", "Clients that trust this root", NotificationSeverity.Critical)]
    [InlineData(CertificateSource.SelfSigned, -2, "Certificate app.example.com has expired", "Create a new one", NotificationSeverity.Critical)]
    public void AnExpiryNamesTheCertificateTheDaysAndWhatToDoForItsSource(CertificateSource source, double days, string title, string action, NotificationSeverity severity)
    {
        var e = NotificationText.CertificateExpiring("app.example.com", CertificateId.New(), source, At.AddDays(days), At);

        Assert.Equal(title, e.Title);
        Assert.Contains(action, e.Body, StringComparison.Ordinal);
        Assert.Equal(severity, e.Severity);
        Assert.Equal("/certificates", e.LinkPath);
    }

    [Fact]
    public void AcmeFailureAndRecoveryAndSignInEventsAreSpecific()
    {
        var failed = NotificationText.AcmeFailed("app.example.com", CertificateId.New(), 1, "rateLimited: too many orders", At.AddHours(1), At.AddDays(20), At);
        Assert.Equal("Issuing app.example.com failed", failed.Title);
        Assert.Equal("The CA said: rateLimited: too many orders. Paduraksa retries at 15:02 UTC. The current certificate is valid until 2026-12-02. Check the order on the Certificates page.", failed.Body);

        var recovered = NotificationText.AcmeRecovered("app.example.com", CertificateId.New(), At.AddDays(90), At);
        Assert.Equal("app.example.com was issued after a failure", recovered.Title);

        var locked = NotificationText.SignInLockout("ops", UserId.New(), At.AddMinutes(15), At);
        Assert.Equal("Account ops is locked", locked.Title);
        Assert.Contains("refused until 14:17 UTC", locked.Body, StringComparison.Ordinal);

        var address = NotificationText.SignInNewAddress("ops", UserId.New(), "203.0.113.9", At);
        Assert.Equal("ops signed in from a new address", address.Title);
        Assert.Contains("203.0.113.9 has not signed in as this account in the last 30 days", address.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ADigestKeepsTheWorstSeverityTheEarliestTimeAndTenLines()
    {
        List<NotificationEvent> events = [.. Enumerable.Range(0, 13).Select(i => new NotificationEvent(
            NotificationEventKind.UpstreamDown, $"Upstream u{i} is down", "b", i == 4 ? NotificationSeverity.Critical : NotificationSeverity.Warning, At.AddSeconds(i)))];

        var digest = NotificationText.Digest(events);

        Assert.Equal("13 notifications: upstreams down", digest.Title);
        Assert.Equal(11, digest.Body.Split('\n').Length);
        Assert.EndsWith("- and 3 more", digest.Body, StringComparison.Ordinal);
        Assert.Equal((NotificationSeverity.Critical, At), (digest.Severity, digest.At));
    }
}
