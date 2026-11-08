using System.Globalization;
using System.Text;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Totp;

namespace Paduraksa.Tests.Vault.Totp;

/// <summary>RFC 6238 arithmetic and <see cref="ITotpAuthenticator"/>: enrolment, verification, replay protection, deletion.</summary>
public sealed class TotpTests : IDisposable
{
    private static readonly byte[] Rfc6238Key = "12345678901234567890"u8.ToArray();

    private readonly VaultHarness _vault = new();

    private ITotpAuthenticator Totp => _vault.Get<ITotpAuthenticator>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    [Theory]
    [InlineData(59L, 94287082)]
    [InlineData(1111111109L, 07081804)]
    [InlineData(1111111111L, 14050471)]
    [InlineData(1234567890L, 89005924)]
    [InlineData(2000000000L, 69279037)]
    [InlineData(20000000000L, 65353130)]
    public void MatchesTheRfc6238Vectors(long unixSeconds, int expected) =>
        Assert.Equal(expected, TotpMath.Code(Rfc6238Key, unixSeconds / TotpMath.StepSeconds, digits: 8));

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32MatchesRfc4648WithoutPadding(string text, string expected) =>
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public async Task EnrolmentReturnsTheUriOnceAndStoresASealedPendingSecret()
    {
        var enrollment = await Totp.BeginEnrollmentAsync("alice@example.com", "Paduraksa", Ct);

        var uri = enrollment.ProvisioningUri;
        Assert.Equal("otpauth", uri.Scheme);
        Assert.Equal("totp", uri.Host);
        Assert.Equal("/Paduraksa:alice%40example.com", uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("Paduraksa", query["issuer"]);
        Assert.Equal("SHA1", query["algorithm"]);
        Assert.Equal("6", query["digits"]);
        Assert.Equal("30", query["period"]);
        Assert.Equal(32, query["secret"]!.Length); // 160 bits of base32

        var metadata = await _vault.Get<ISecretStore>().DescribeAsync(enrollment.SecretId, Ct);
        Assert.Equal(SecretKind.TotpSecret, metadata!.Kind);

        // On disk the secret is sealed: its bytes appear nowhere in the row.
        var secret = SecretFrom(uri);
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        var row = await context.Secrets.AsNoTracking().SingleAsync(Ct);
        Assert.DoesNotContain(Convert.ToHexString(secret), Convert.ToHexString(row.Sealed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachEnrolmentGetsItsOwnSecret()
    {
        var first = await Totp.BeginEnrollmentAsync("a", "Paduraksa", Ct);
        var second = await Totp.BeginEnrollmentAsync("a", "Paduraksa", Ct);

        Assert.NotEqual(first.SecretId, second.SecretId);
        Assert.NotEqual(first.ProvisioningUri.Query, second.ProvisioningUri.Query);
    }

    [Theory]
    [InlineData("", "Paduraksa")]
    [InlineData("a", "")]
    [InlineData("a", "Pa:duraksa")]
    [InlineData("a\nb", "Paduraksa")]
    public async Task BadLabelsAreRefused(string account, string issuer) =>
        await Assert.ThrowsAsync<ArgumentException>(async () => await Totp.BeginEnrollmentAsync(account, issuer, Ct));

    [Fact]
    public async Task APendingSecretVerifiesNothingUntilConfirmed()
    {
        var (id, secret) = await EnrollAsync();

        Assert.False(await Totp.VerifyAsync(id, CodeNow(secret), Ct));
        Assert.True(await Totp.ConfirmEnrollmentAsync(id, CodeNow(secret), Ct));
    }

    [Fact]
    public async Task AConfirmedSecretCannotBeConfirmedAgain()
    {
        var (id, secret) = await EnrollAsync();
        Assert.True(await Totp.ConfirmEnrollmentAsync(id, CodeNow(secret), Ct));

        _vault.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.False(await Totp.ConfirmEnrollmentAsync(id, CodeNow(secret), Ct));
    }

    [Fact]
    public async Task AWrongCodeIsRefusedAndConfirmsNothing()
    {
        var (id, secret) = await EnrollAsync();
        var wrong = ((int.Parse(CodeNow(secret), CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        Assert.False(await Totp.ConfirmEnrollmentAsync(id, wrong, Ct));
        Assert.True(await Totp.ConfirmEnrollmentAsync(id, CodeNow(secret), Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    [InlineData("١٢٣٤٥٦")] // Arabic-Indic digits are not ASCII digits
    public async Task CodesThatAreNotSixAsciiDigitsAreRefused(string code)
    {
        var (id, secret) = await EnrollAsync();
        await Totp.ConfirmEnrollmentAsync(id, CodeNow(secret), Ct);

        Assert.False(await Totp.VerifyAsync(id, code, Ct));
    }

    [Fact]
    public async Task ACodeIsAcceptedOnceAndNeverAgain()
    {
        var (id, secret) = await ConfirmedAsync();
        _vault.Time.Advance(TimeSpan.FromSeconds(30));
        var code = CodeNow(secret);

        Assert.True(await Totp.VerifyAsync(id, code, Ct));
        Assert.False(await Totp.VerifyAsync(id, code, Ct));
    }

    [Fact]
    public async Task ACodeUsedToConfirmCannotLogIn()
    {
        var (id, secret) = await EnrollAsync();
        var code = CodeNow(secret);
        Assert.True(await Totp.ConfirmEnrollmentAsync(id, code, Ct));

        Assert.False(await Totp.VerifyAsync(id, code, Ct));
    }

    [Fact]
    public async Task EarlierStepsAreRefusedOnceALaterOneWasUsed()
    {
        var (id, secret) = await ConfirmedAsync();
        _vault.Time.Advance(TimeSpan.FromSeconds(60));
        var current = StepNow();
        var previous = CodeAt(secret, current - 1);
        var next = CodeAt(secret, current + 1);

        Assert.True(await Totp.VerifyAsync(id, next, Ct)); // one step ahead is inside the window
        Assert.False(await Totp.VerifyAsync(id, previous, Ct)); // and now everything before it is spent
        Assert.False(await Totp.VerifyAsync(id, CodeAt(secret, current), Ct));
    }

    [Fact]
    public async Task OneStepOfDriftEitherWayIsAcceptedButNotTwo()
    {
        var (id, secret) = await ConfirmedAsync();
        _vault.Time.Advance(TimeSpan.FromSeconds(300));
        var current = StepNow();

        Assert.False(await Totp.VerifyAsync(id, CodeAt(secret, current + 2), Ct));
        Assert.False(await Totp.VerifyAsync(id, CodeAt(secret, current - 2), Ct));
        Assert.True(await Totp.VerifyAsync(id, CodeAt(secret, current - 1), Ct));
    }

    [Fact]
    public async Task ReplayProtectionSurvivesARestart()
    {
        var (id, secret) = await ConfirmedAsync();
        _vault.Time.Advance(TimeSpan.FromSeconds(30));
        var code = CodeNow(secret);
        Assert.True(await Totp.VerifyAsync(id, code, Ct));

        _vault.Restart();

        Assert.False(await _vault.Get<ITotpAuthenticator>().VerifyAsync(id, code, Ct));
    }

    [Fact]
    public async Task TwoSimultaneousUsesOfOneCodeSucceedExactlyOnce()
    {
        var (id, secret) = await ConfirmedAsync();
        _vault.Time.Advance(TimeSpan.FromSeconds(30));
        var code = CodeNow(secret);
        var other = _vault.Another().GetRequiredService<ITotpAuthenticator>();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => Task.Run(async () => await (i % 2 == 0 ? Totp : other).VerifyAsync(id, code, Ct), Ct)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task AnUnknownOrForeignSecretIsJustWrong()
    {
        var other = await _vault.Get<ISecretStore>().PutAsync(
            SecretKind.DnsApiToken, "token"u8.ToArray(), new SecretOptions("dns", null, ["api.example.com"]), Ct);

        Assert.False(await Totp.VerifyAsync(SecretId.New(), "123456", Ct));
        Assert.False(await Totp.ConfirmEnrollmentAsync(other, "123456", Ct));
        Assert.False(await Totp.DeleteAsync(other, Ct));
        Assert.NotNull(await _vault.Get<ISecretStore>().DescribeAsync(other, Ct));
    }

    [Fact]
    public async Task OnlyTheTotpCapabilityCreatesOrReplacesATotpSecret()
    {
        var store = _vault.Get<ISecretStore>();
        var (id, _) = await EnrollAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.PutAsync(SecretKind.TotpSecret, "value"u8.ToArray(), new(null, null, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReplaceAsync(id, "value"u8.ToArray(), null, Ct));
    }

    [Fact]
    public async Task EnrolmentChecksTheKeyAgainstTheDatabaseLikeEverySeal()
    {
        // Another process rotated the master key: this one must refuse to seal under the old one.
        using var other = Kek.Generate();
        await Totp.BeginEnrollmentAsync("alice", "Paduraksa", Ct);
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            var meta = await context.Meta.SingleAsync(Ct);
            meta.KekCheck = other.CheckValue;
            await context.SaveChangesAsync(Ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Totp.BeginEnrollmentAsync("bob", "Paduraksa", Ct));
        await using var check = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Equal(1, await check.Secrets.CountAsync(Ct));
    }

    [Fact]
    public async Task DeleteRemovesTheSecretAndItsState()
    {
        var (id, secret) = await ConfirmedAsync();

        Assert.True(await Totp.DeleteAsync(id, Ct));

        Assert.False(await Totp.DeleteAsync(id, Ct));
        _vault.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await Totp.VerifyAsync(id, CodeNow(secret), Ct));
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Empty(await context.TotpStates.ToListAsync(Ct));
    }

    [Fact]
    public async Task DeletingTheSecretThroughTheStoreRemovesTheStateToo()
    {
        var (id, _) = await EnrollAsync();

        Assert.True(await _vault.Get<ISecretStore>().DeleteAsync(id, Ct));

        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Empty(await context.TotpStates.ToListAsync(Ct));
    }

    [Fact]
    public void TheInterfaceReturnsNoSecretAfterEnrolment()
    {
        // The only member that returns anything but a bool is the enrolment, once (the architecture tests also hold the
        // contract to plain data).
        var returns = typeof(ITotpAuthenticator).GetMethods().Select(m => m.ReturnType).ToList();
        Assert.Equal(1, returns.Count(t => t != typeof(ValueTask<bool>)));
        Assert.Contains(typeof(ValueTask<TotpEnrollment>), returns);
    }

    private async Task<(SecretId Id, byte[] Secret)> EnrollAsync()
    {
        var enrollment = await Totp.BeginEnrollmentAsync("alice", "Paduraksa", Ct);
        return (enrollment.SecretId, SecretFrom(enrollment.ProvisioningUri));
    }

    private async Task<(SecretId Id, byte[] Secret)> ConfirmedAsync()
    {
        var (id, secret) = await EnrollAsync();
        Assert.True(await Totp.ConfirmEnrollmentAsync(id, CodeNow(secret), Ct));
        return (id, secret);
    }

    private long StepNow() => TotpMath.StepOf(_vault.Time.Now);

    private string CodeNow(byte[] secret) => CodeAt(secret, StepNow());

    private static string CodeAt(byte[] secret, long step) =>
        TotpMath.Code(secret, step).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>The test's own authenticator: reads the shared secret the way a phone does, from the URI.</summary>
    private static byte[] SecretFrom(Uri uri)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = HttpUtility.ParseQueryString(uri.Query)["secret"]!;
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
