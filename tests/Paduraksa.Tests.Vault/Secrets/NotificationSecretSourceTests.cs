using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;

namespace Paduraksa.Tests.Vault.Secrets;

public sealed class NotificationSecretSourceTests : IDisposable
{
    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISecretStore Store => _vault.Get<ISecretStore>();

    private INotificationSecretSource Source => _vault.Get<INotificationSecretSource>();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task LendsTheValueToTheCallbackAndNothingElse()
    {
        var id = await Store.PutAsync(SecretKind.NotificationSecret, Encoding.UTF8.GetBytes("https://discord.example/api/webhooks/1/secret"), new("hook", null, null), Ct);
        string? seen = null;

        var found = await Source.UseAsync(id, (value, _) =>
        {
            seen = Encoding.UTF8.GetString(value.Span);
            return ValueTask.CompletedTask;
        }, Ct);

        Assert.True(found);
        Assert.Equal("https://discord.example/api/webhooks/1/secret", seen);
        Assert.DoesNotContain("secret", (await Store.DescribeAsync(id, Ct))!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroesTheBufferWhenTheCallbackReturns()
    {
        var id = await Store.PutAsync(SecretKind.NotificationSecret, "smtp-password"u8.ToArray(), new(null, null, null), Ct);
        ReadOnlyMemory<byte> kept = default;

        await Source.UseAsync(id, (value, _) =>
        {
            kept = value;
            return ValueTask.CompletedTask;
        }, Ct);

        Assert.All(kept.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task AnotherKindOfSecretOrAMissingOneIsNotFoundAndTheCallbackNeverRuns()
    {
        var dns = await Store.PutAsync(SecretKind.DnsApiToken, "dns-token"u8.ToArray(), new(null, null, ["api.example.com"]), Ct);
        var mac = await Store.PutAsync(SecretKind.HmacSigningKey, "mac-key"u8.ToArray(), new(null, MacAlgorithm.HmacSha256, null), Ct);
        var called = false;
        ValueTask Use(ReadOnlyMemory<byte> value, CancellationToken token)
        {
            called = true;
            return ValueTask.CompletedTask;
        }

        Assert.False(await Source.UseAsync(dns, Use, Ct));
        Assert.False(await Source.UseAsync(mac, Use, Ct));
        Assert.False(await Source.UseAsync(SecretId.New(), Use, Ct));
        Assert.False(called);
    }

    [Fact]
    public async Task ARotatedValueIsWhatTheNextUseSees()
    {
        var id = await Store.PutAsync(SecretKind.NotificationSecret, "first"u8.ToArray(), new(null, null, null), Ct);
        await Store.ReplaceAsync(id, "second"u8.ToArray(), null, Ct);
        string? seen = null;

        await Source.UseAsync(id, (value, _) =>
        {
            seen = Encoding.UTF8.GetString(value.Span);
            return ValueTask.CompletedTask;
        }, Ct);

        Assert.Equal("second", seen);
    }

    [Fact]
    public async Task ANotificationSecretTakesNoMacAlgorithmAndNoAllowedHosts()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Store.PutAsync(SecretKind.NotificationSecret, "x"u8.ToArray(), new(null, MacAlgorithm.HmacSha256, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Store.PutAsync(SecretKind.NotificationSecret, "x"u8.ToArray(), new(null, null, ["example.com"]), Ct));
    }
}
