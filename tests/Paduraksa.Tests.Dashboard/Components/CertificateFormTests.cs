using System.Text;

using Bunit;

using Microsoft.AspNetCore.Components.Forms;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard.Certificates;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The upload form: the file is required and capped, the stream and password reach the vault call, the password field is
/// emptied as soon as the form is sent and its array cleared after the call, and a refusal is shown with the service's words.
/// </summary>
public sealed class CertificateFormTests
{
    private const string Pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";

    [Fact]
    public async Task WithoutAFile_NothingIsSent()
    {
        await using DashboardTestContext context = new();
        var calls = 0;
        var form = Render(context, _ => { calls++; return Task.FromResult<CertificateOperationResult?>(null); });

        form.Find(".pdk-upload-submit").Click();

        Assert.Equal(0, calls);
        Assert.Contains("Choose a PEM or PFX file first.", form.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileOverTheCap_IsRefusedBeforeAnythingIsSent()
    {
        await using DashboardTestContext context = new();
        var calls = 0;
        var form = Render(context, _ => { calls++; return Task.FromResult<CertificateOperationResult?>(null); });

        form.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[CertificateImportLimits.MaxBytes + 1], "big.pem"));
        form.Find(".pdk-upload-submit").Click();

        Assert.Equal(0, calls);
        Assert.Contains("larger than 1 MiB", form.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpload_StreamsTheFile_PassesThePassword_ThenClearsBoth()
    {
        await using DashboardTestContext context = new();
        string? received = null;
        string? passwordSeen = null;
        ReadOnlyMemory<char> passwordMemory = default;
        string? fieldDuringCall = null;
        IRenderedComponent<UploadCertificateForm>? form = null;
        CertificateSummary? uploaded = null;
        form = Render(
            context,
            async upload =>
            {
                using StreamReader reader = new(upload.Data, Encoding.ASCII);
                received = await reader.ReadToEndAsync();
                // Let the form render while the call is in flight: its field must be empty by then.
                await Task.Yield();
                passwordSeen = new string(upload.Password.Span);
                passwordMemory = upload.Password;
                fieldDuringCall = form!.Find(".pdk-upload-password input").GetAttribute("value");
                return new CertificateOperationResult(CertificateOperationStatus.Completed, Summary("pfx-label"));
            },
            summary => uploaded = summary);

        form.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(Pem, "site.pfx"));
        form.Find(".pdk-upload-password input").Change("s3cret-pfx");
        form.Find(".pdk-upload-label input").Change("pfx-label");
        await form.InvokeAsync(() => form.Find(".pdk-upload-submit").Click());
        form.WaitForAssertion(() => Assert.NotNull(uploaded));

        Assert.Equal(Pem, received);
        Assert.Equal("s3cret-pfx", passwordSeen);
        // Emptied before the call was even made, and the array the call saw is zeroed afterwards.
        Assert.True(string.IsNullOrEmpty(fieldDuringCall));
        Assert.True(passwordMemory.Span.ToArray().All(c => c == '\0'));
        Assert.True(string.IsNullOrEmpty(form.Find(".pdk-upload-password input").GetAttribute("value")));
        Assert.Equal("pfx-label", uploaded?.Label);
        Assert.DoesNotContain("s3cret-pfx", form.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusal_ShowsTheReason_AndThePasswordIsGoneAnyway()
    {
        await using DashboardTestContext context = new();
        var form = Render(context, _ => Task.FromResult<CertificateOperationResult?>(new CertificateOperationResult(
            CertificateOperationStatus.Invalid, Errors: [new ConfigError(string.Empty, "certificate.import.wrongPassword", "the password is wrong")])));

        form.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(Pem, "site.p12"));
        form.Find(".pdk-upload-password input").Change("wrong");
        await form.InvokeAsync(() => form.Find(".pdk-upload-submit").Click());

        Assert.Contains("The password is wrong.", form.Markup, StringComparison.Ordinal);
        Assert.Contains("Choose the file again", form.Markup, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(form.Find(".pdk-upload-password input").GetAttribute("value")));
        Assert.Equal("password", form.Find(".pdk-upload-password input").GetAttribute("type"));
    }

    [Fact]
    public void Names_AreSplitAndCheckedLikeTheService()
    {
        Assert.Equal(["a.example.com", "*.example.com", "192.0.2.1"], NameList.Parse("a.example.com, *.example.com\n192.0.2.1 A.example.com"));
        Assert.Null(NameList.Problem(["a.example.com", "*.example.com"]));
        Assert.Contains("wildcard", NameList.Problem(["*.example.com"], allowWildcards: false), StringComparison.Ordinal);
        Assert.Contains("IP address", NameList.Problem(["192.0.2.1"], allowIps: false), StringComparison.Ordinal);
        Assert.NotNull(NameList.Problem(["not a name"]));
        Assert.NotNull(NameList.Problem([]));
    }

    [Theory]
    [InlineData(90, "success", "3 months left")]
    [InlineData(20, "warning", "20 days left")]
    [InlineData(3, "error", "3 days left")]
    [InlineData(-2, "error", "Expired 2 days ago")]
    public void TheExpiryCountdown_IsColouredBySoonness(int days, string tone, string text)
    {
        DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var notAfter = now.AddDays(days).AddMinutes(days >= 0 ? 1 : -1);

        Assert.Equal(tone, CertificateDisplay.ExpiryTone(notAfter, now));
        Assert.Equal(text, CertificateDisplay.ExpiresIn(notAfter, now));
    }

    private static IRenderedComponent<UploadCertificateForm> Render(
        DashboardTestContext context, Func<CertificateUpload, Task<CertificateOperationResult?>> submit, Action<CertificateSummary>? uploaded = null) =>
        context.Render<UploadCertificateForm>(parameters => parameters
            .Add(f => f.Submit, submit)
            .Add(f => f.Uploaded, uploaded ?? (_ => { })));

    internal static CertificateSummary Summary(string? label = null, params string[] names) => new(
        CertificateId.New(), CertificateSource.Uploaded, CertificateStatus.Active, CertificateValidity.Valid, names.Length == 0 ? ["example.com"] : names,
        DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(60), "CN=example.com", "CN=Test", "01", new string('a', 64), "ECDSA P-256",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Label: label);
}
