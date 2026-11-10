using System.Text.Json;

using Bunit;

using Microsoft.AspNetCore.Components;

using MudBlazor;

using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.AccountArea;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class AccountAndAuditTests
{
    [Fact]
    public async Task TheTotpEnrolment_GoesFromPasswordToCodeToRecoveryCodes_AndDropsEachSecret()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, _) = IdentityPages.SignIn(context, Role.Viewer);
        context.Render<MudPopoverProvider>();
        var enabled = false;
        var step = context.Render<TwoStepEnrollment>(parameters => parameters
            .Add(p => p.Enabled, EventCallback.Factory.Create(this, () => enabled = true)));

        Assert.Equal("password", Phase(step));
        await step.Find(".pdk-totp-password input").ChangeAsync(new() { Value = "wrong" });
        await step.Find(".pdk-totp-begin").ClickAsync(new());
        Assert.Contains("That password is wrong.", step.Markup, StringComparison.Ordinal);
        Assert.Equal("password", Phase(step));

        await step.Find(".pdk-totp-password input").ChangeAsync(new() { Value = FakeDashboardIdentity.Password });
        await step.Find(".pdk-totp-begin").ClickAsync(new());
        Assert.Equal("scan", Phase(step));
        Assert.Single(step.FindAll("svg.pdk-qr path"));
        Assert.Contains("JBSW Y3DP", step.Find(".pdk-totp-secret").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeDashboardIdentity.Password, step.Markup, StringComparison.Ordinal);

        await step.Find(".pdk-totp-code input").ChangeAsync(new() { Value = "000000" });
        await step.Find(".pdk-totp-confirm").ClickAsync(new());
        Assert.Contains("That code didn't work.", step.Markup, StringComparison.Ordinal);
        Assert.Equal("scan", Phase(step));

        await step.Find(".pdk-totp-code input").ChangeAsync(new() { Value = "123 456" });
        await step.Find(".pdk-totp-confirm").ClickAsync(new());
        Assert.Equal("codes", Phase(step));
        Assert.Equal(10, step.FindAll(".pdk-recovery-codes li").Count);
        Assert.Empty(step.FindAll("svg.pdk-qr"));
        Assert.DoesNotContain("JBSWY3DP", step.Markup, StringComparison.Ordinal);
        Assert.Single(step.FindAll("[data-action=download-codes]"));
        Assert.Single(step.FindAll("[data-action=copy-codes]"));

        await step.Find(".pdk-totp-saved input").ChangeAsync(new() { Value = true });
        await step.Find(".pdk-totp-finish").ClickAsync(new());
        Assert.Equal("enabled", Phase(step));
        Assert.True(enabled);
        Assert.Empty(step.FindAll(".pdk-recovery-codes li"));
        Assert.Equal(["begin", "begin", "confirm", "confirm"], identity.Calls);
    }

    [Fact]
    public async Task NewRecoveryCodes_AreShownOnce()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, _) = IdentityPages.SignIn(context, Role.Viewer);
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<DrawerHost>();
        var card = context.Render<TwoFactorCard>(parameters => parameters.Add(p => p.Enabled, true));

        var opening = card.Find("[data-action=regenerate]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("input[data-field=confirm-password]").Count == 1);
        await dialogs.Find("input[data-field=confirm-password]").ChangeAsync(new() { Value = FakeDashboardIdentity.Password });
        await dialogs.Find("[data-action=confirm-password]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-recovery-codes li").Count == 10);
        Assert.Contains(identity.RecoveryCodes[0], dialogs.Markup, StringComparison.Ordinal);

        // Only "I have saved them" closes the drawer now.
        await dialogs.InvokeAsync(dialogs.Instance.EscapeAsync);
        Assert.Equal(10, dialogs.FindAll(".pdk-recovery-codes li").Count);
        await dialogs.Find("[data-action=codes-saved]").ClickAsync(new());
        await opening.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(dialogs.FindAll(".pdk-recovery-codes"));
        Assert.DoesNotContain(identity.RecoveryCodes[0], dialogs.Markup, StringComparison.Ordinal);
        // Seeing them again means making new ones, behind the password again.
        _ = card.Find("[data-action=regenerate]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll("input[data-field=confirm-password]").Count == 1);
        Assert.Empty(dialogs.FindAll(".pdk-recovery-codes"));
    }

    [Fact]
    public void TheRecoveryCodesFile_NamesTheAccountAndHasOneCodePerLine()
    {
        var text = RecoveryCodesView.FileText(["aaaa-bbbb", "cccc-dddd"], "ada");

        Assert.StartsWith("Paduraksa recovery codes for ada\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\naaaa-bbbb\ncccc-dddd\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MyAccount_ListsTheSessions_AndEndsAnotherOne()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, user) = IdentityPages.SignIn(context, Role.Viewer);
        var other = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        identity.Sessions.Add(new SessionSummary(user.Session.Session.SessionId, "192.168.1.20", "Mozilla/5.0 (X11; Linux x86_64) Firefox/140.0", now, now, now.AddHours(8), true, false));
        identity.Sessions.Add(new SessionSummary(other, "10.0.0.7", "Mozilla/5.0 (iPhone) Safari/605.1", now.AddDays(-1), now.AddHours(-2), now.AddHours(4), true, false));
        var dialogs = context.Render<MudDialogProvider>();
        context.Render<MudPopoverProvider>();
        var page = context.Render<MyAccount>();
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 2);

        Assert.Contains("This session", page.Find("[data-current=true]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Firefox on Linux", page.Find("[data-current=true]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Safari on iOS", page.Find("[data-current=false]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.Find("[data-current=true]").QuerySelectorAll("[data-action=revoke-session]"));
        Assert.Contains("Member since", page.Find("[data-card=profile]").TextContent, StringComparison.Ordinal);
        Assert.True(page.Find("input[data-field=username]").HasAttribute("readonly"));
        Assert.Equal("off", page.Find("[data-card=two-factor]").GetAttribute("data-state"));

        // The click waits for the dialog's answer, so it isn't awaited here.
        var asking = page.Find("[data-current=false] [data-action=revoke-session]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        await dialogs.Find(".pdk-confirm").ClickAsync(new());
        await asking;

        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);
        Assert.Contains($"revoke {other}", identity.Calls);
    }

    [Fact]
    public async Task ChangingThePassword_SaysTheOtherSessionsEnded_AndEmptiesTheFields()
    {
        await using DashboardTestContext context = new();
        var (identity, _, _, _) = IdentityPages.SignIn(context, Role.Viewer);
        context.Render<MudPopoverProvider>();
        var card = context.Render<PasswordCard>();

        await card.Find("input[data-field=current-password]").InputAsync(new() { Value = FakeDashboardIdentity.Password });
        await card.Find("input[data-field=new-password]").InputAsync(new() { Value = "short" });
        Assert.Equal("0", card.Find("[data-strength]").GetAttribute("data-strength"));
        await card.Find("input[data-field=new-password]").InputAsync(new() { Value = "a much longer Passphrase 42!" });
        Assert.Equal("4", card.Find("[data-strength]").GetAttribute("data-strength"));
        await card.Find("input[data-field=repeat-password]").InputAsync(new() { Value = "a much longer Passphrase 42!" });
        await card.Find("form").SubmitAsync();

        await card.WaitForStateAsync(() => card.FindAll("[data-result=changed]").Count == 1);
        Assert.Contains("other sessions were signed out", card.Find("[data-result=changed]").TextContent, StringComparison.Ordinal);
        Assert.All(card.FindAll("input[type=password]"), input => Assert.True(string.IsNullOrEmpty(input.GetAttribute("value"))));
        Assert.Equal(["password"], identity.Calls);
    }

    [Fact]
    public async Task TheAuditPage_FiltersByOutcome_AndShowsTheChain()
    {
        await using DashboardTestContext context = new();
        var (_, audit, _, _) = IdentityPages.SignIn(context, Role.Auditor);
        audit.Events.Add(Event(1, "auth.login", AuditOutcome.Success, details: """{"method":"password"}"""));
        audit.Events.Add(Event(2, "config.host.delete", AuditOutcome.Denied, target: HostId.New().ToString()));
        audit.Verification = new AuditVerification(true, 2, audit.Events[1].Id, new string('a', 64));
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<Audit>();

        await page.WaitForStateAsync(() => page.FindAll("[data-event]").Count == 2 && page.Find("[data-section=chain]").GetAttribute("data-chain") == "intact");
        Assert.Contains("Chain intact: 2 events verify", page.Find("[data-section=chain]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Signed in", page.Markup, StringComparison.Ordinal);
        Assert.NotNull(audit.Queries[0].From);

        // A row opens its details in a drawer, pretty-printed, and a target with a page links to it.
        _ = page.Find("[data-event] button").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=audit-event]").Count == 1);
        Assert.Single(drawers.FindAll("a[href^='/hosts/host_']"));
        Assert.Equal("Denied", drawers.Find("[data-chip=outcome]").TextContent.Trim());
        await drawers.InvokeAsync(drawers.Instance.EscapeAsync);
        Assert.Empty(drawers.FindAll(".pdk-sheet"));
        _ = page.Find("[data-action='auth.login'] button").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=audit-event] pre").Count == 1);
        Assert.Contains("\"method\": \"password\"", drawers.Find("[data-drawer=audit-event] pre").TextContent, StringComparison.Ordinal);
        await drawers.InvokeAsync(drawers.Instance.EscapeAsync);

        await page.Find("[data-field=range] button:last-of-type").ClickAsync(new());
        await page.InvokeAsync(() => SetOutcome(page, AuditOutcome.Denied));
        await page.WaitForStateAsync(() => page.FindAll("[data-event]").Count == 1);
        Assert.Equal(AuditOutcome.Denied, audit.Queries[^1].Outcome);
        Assert.Equal("denied", page.Find("[data-event]").GetAttribute("data-outcome"));

        audit.Verification = new AuditVerification(false, 1, audit.Events[0].Id, null, new AuditBreak(audit.Events[1].Id, "hashMismatch", "The event's hash doesn't match its contents."));
        await page.Find("[data-action=verify]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.Find("[data-section=chain]").GetAttribute("data-chain") == "broken");
        Assert.Equal(audit.Events[1].Id.ToString(), page.Find("[data-break]").GetAttribute("data-break"));
        Assert.Contains("doesn't match its contents", page.Find("[data-section=chain]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAuditPage_SurvivesAStoreThatFails_AndTriesAgain()
    {
        await using DashboardTestContext context = new();
        var (_, audit, _, _) = IdentityPages.SignIn(context, Role.Auditor);
        audit.Events.Add(Event(1, "auth.login", AuditOutcome.Success));
        audit.Failing = true;
        context.Render<MudPopoverProvider>();
        context.Render<MudSnackbarProvider>();
        var page = context.Render<Audit>();

        await page.WaitForStateAsync(() => page.FindAll("[data-alert=load-failed]").Count == 1);
        Assert.Contains("The events couldn't be read.", page.Find("[data-alert=load-failed]").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll(".pdk-row-skeleton"));

        // A filter change that fails again keeps the page (and the circuit) alive.
        await page.InvokeAsync(() => SetOutcome(page, AuditOutcome.Denied));
        Assert.Single(page.FindAll("[data-alert=load-failed]"));

        audit.Failing = false;
        await page.InvokeAsync(() => SetOutcome(page, AuditOutcome.Success));
        await page.WaitForStateAsync(() => page.FindAll("[data-event]").Count == 1);
        Assert.Empty(page.FindAll("[data-alert=load-failed]"));
    }

    [Theory]
    [InlineData(Role.Auditor, true)]
    [InlineData(Role.Operator, false)]
    public async Task TheAuditPage_NeedsAuditRead(Role role, bool shown)
    {
        await using DashboardTestContext context = new();
        IdentityPages.SignIn(context, role);
        context.Render<MudPopoverProvider>();
        var page = context.Render<Audit>();

        await page.WaitForStateAsync(() => page.FindAll("[data-section=events]").Count == 1 || page.Markup.Contains("can't read the audit log", StringComparison.Ordinal));
        Assert.Equal(shown, page.FindAll("[data-section=chain]").Count == 1);
        Assert.Equal(shown, page.FindAll("[data-action=export]").Count == 1);
    }

    [Fact]
    public void AnExportLine_IsTheEventAsTheApiNamesIt()
    {
        var e = Event(1, "token.issue", AuditOutcome.Success, details: """{"name":"ci"}""");
        System.Buffers.ArrayBufferWriter<byte> output = new();

        Paduraksa.Dashboard.Audit.AuditText.WriteLine(output, e);

        var line = System.Text.Encoding.UTF8.GetString(output.WrittenSpan);
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("token.issue", json.RootElement.GetProperty("action").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("dashboard", json.RootElement.GetProperty("channel").GetString());
        Assert.Equal("user", json.RootElement.GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal("ci", json.RootElement.GetProperty("details").GetProperty("name").GetString());
    }

    private static void SetOutcome(IRenderedComponent<Audit> page, AuditOutcome outcome)
    {
        // The outcome select is a MudSelect (a popover); set it as its ValueChanged would.
        var select = page.FindComponent<MudSelect<AuditOutcome?>>();
        _ = select.Instance.ValueChanged.InvokeAsync(outcome);
    }

    private static string Phase(IRenderedComponent<TwoStepEnrollment> step) => step.Find(".pdk-two-step").GetAttribute("data-phase")!;

    private static AuditEventSummary Event(long order, string action, AuditOutcome outcome, string? target = null, string? details = null) =>
        new(
            AuditEventId.FromValue((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 16) + order),
            DateTimeOffset.UtcNow.AddMinutes(-10 + order),
            new ActorRef(ActorKind.User, UserId.New()),
            Channel.Dashboard,
            action,
            outcome,
            target,
            "192.168.1.20",
            details is null ? null : JsonDocument.Parse(details).RootElement.Clone());
}
