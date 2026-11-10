using System.Reflection;

using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Web;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Pages.Account;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The sign-in pages: plain HTML forms that post, with an antiforgery token, to the admin app's form endpoints; they show only
/// known messages for the codes a failed step redirects with, and never carry a return address that leaves the site.
/// </summary>
public sealed class SignInPageTests
{
    [Fact]
    public void SignIn_IsAPlainFormPostingTheCredentialsWithAnAntiforgeryToken()
    {
        using DashboardTestContext context = new();
        var page = Render<SignIn>(context, DashboardRoutes.SignIn);

        var form = page.Find("form");
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal(DashboardRoutes.Actions.SignIn, form.GetAttribute("action"));
        Assert.Equal(DashboardTestContext.AntiforgeryValue, page.Find("input[name=__RequestVerificationToken]").GetAttribute("value"));
        Assert.Equal("username", page.Find($"input[name={DashboardForm.Username}]").GetAttribute("autocomplete"));
        var password = page.Find($"input[name={DashboardForm.Password}]");
        Assert.Equal("password", password.GetAttribute("type"));
        Assert.Equal("current-password", password.GetAttribute("autocomplete"));
        Assert.Null(password.GetAttribute("value"));
        Assert.Empty(page.FindAll($"input[name={DashboardForm.ReturnUrl}]"));
        Assert.Empty(page.FindAll(".pdk-callout"));
    }

    [Theory]
    [InlineData("auth.invalidCredentials", "The username or password is wrong.")]
    [InlineData("auth.rateLimited", "Too many attempts. Wait a minute, then try again.")]
    [InlineData("csrf.refused", "The form expired. Please try again.")]
    [InlineData("<script>alert(1)</script>", "That didn't work. Please try again.")]
    public void SignIn_ShowsTheMessageOfAKnownCode_AndOnlyAGenericOneOtherwise(string code, string message)
    {
        using DashboardTestContext context = new();
        var page = Render<SignIn>(context, $"{DashboardRoutes.SignIn}?error={Uri.EscapeDataString(code)}");

        var alert = page.Find(".pdk-callout-error");
        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Equal(message, alert.TextContent);
        Assert.DoesNotContain("<script>", page.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("signedOut", "You have signed out.")]
    [InlineData("sessionEnded", "Your session ended. Sign in again to continue.")]
    public void SignIn_ShowsANotice(string notice, string message)
    {
        using DashboardTestContext context = new();
        var page = Render<SignIn>(context, $"{DashboardRoutes.SignIn}?notice={notice}");

        Assert.Equal(message, page.Find(".pdk-callout-info").TextContent);
    }

    [Theory]
    [InlineData("/hosts?tag=media", "/hosts?tag=media")]
    [InlineData("/", "/")]
    [InlineData("//evil.example/", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("https://evil.example/", null)]
    [InlineData("hosts", null)]
    [InlineData("/a b", null)]
    public void SignIn_CarriesALocalReturnAddress_AndDropsAnyOther(string returnUrl, string? carried)
    {
        using DashboardTestContext context = new();
        var page = Render<SignIn>(context, $"{DashboardRoutes.SignIn}?returnUrl={Uri.EscapeDataString(returnUrl)}");

        var hidden = page.FindAll($"input[name={DashboardForm.ReturnUrl}]");
        Assert.Equal(carried, hidden.SingleOrDefault()?.GetAttribute("value"));
    }

    [Fact]
    public void SignIn_SendsASignedInBrowserOnToItsReturnAddress()
    {
        using DashboardTestContext context = new();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"{DashboardRoutes.SignIn}?returnUrl=%2Fcertificates");

        context.Render<SignIn>(parameters => parameters.AddCascadingValue(DashboardTestContext.StateOf(TestUsers.User(Role.Viewer))));

        Assert.EndsWith("/certificates", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_AsksForTheAuthenticatorCode_OrARecoveryCode()
    {
        using DashboardTestContext context = new();
        var code = Render<SignInVerify>(context, $"{DashboardRoutes.SignInVerify}?returnUrl=%2Faudit");

        Assert.Equal(DashboardRoutes.Actions.SecondFactor, code.Find("form").GetAttribute("action"));
        var input = code.Find($"input[name={DashboardForm.Code}]");
        Assert.Equal("numeric", input.GetAttribute("inputmode"));
        Assert.Equal("one-time-code", input.GetAttribute("autocomplete"));
        Assert.Empty(code.FindAll($"input[name={DashboardForm.RecoveryCode}]"));
        Assert.Equal("/audit", code.Find($"input[name={DashboardForm.ReturnUrl}]").GetAttribute("value"));
        Assert.Equal("/signin/verify?recovery=1&returnUrl=%2Faudit", code.Find("a[href*='recovery=1']").GetAttribute("href"));

        using DashboardTestContext recoveryContext = new();
        var recovery = Render<SignInVerify>(recoveryContext, $"{DashboardRoutes.SignInVerify}?recovery=1");
        Assert.Single(recovery.FindAll($"input[name={DashboardForm.RecoveryCode}]"));
        Assert.Empty(recovery.FindAll($"input[name={DashboardForm.Code}]"));
    }

    [Fact]
    public void EveryErrorTheFormEndpointsRedirectWith_HasItsOwnMessage()
    {
        foreach (var code in AdminWebForm.ErrorCodes)
        {
            Assert.Contains(code, SignInMessages.KnownErrors);
        }
    }

    [Fact]
    public void TheFormFieldsAndParameters_AreNamedAsTheFormEndpointsReadThem()
    {
        var admin = Constants(typeof(AdminWebForm));
        var dashboard = Constants(typeof(DashboardForm));

        foreach (var (name, value) in admin.Where(pair => pair.Key != nameof(AdminWebForm.PasswordMismatch)))
        {
            Assert.True(dashboard.TryGetValue(name, out var ours), $"DashboardForm has no {name}.");
            Assert.Equal(value, ours);
        }
    }

    private static IRenderedComponent<TPage> Render<TPage>(DashboardTestContext context, string uri)
        where TPage : IComponent
    {
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        return context.Render<TPage>(parameters => parameters.AddCascadingValue(DashboardTestContext.StateOf(null)));
    }

    private static Dictionary<string, object?> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .ToDictionary(field => field.Name, field => field.GetRawConstantValue());
}
