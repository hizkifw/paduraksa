using System.Text.Json;

using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class AdminJsonContractTests
{
    private static JsonSerializerOptions Http()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        IdentityJsonContext.AddTo(options);
        return options;
    }

    [Fact]
    public void TypedIdsRolesAndPermissionsKeepTheirNames()
    {
        var id = UserId.New();
        UserSummary user = new(id, "alice", "Alice", [Role.Admin, Role.Owner], false, false, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(user, Http());

        Assert.Contains($"\"id\":\"{id}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"roles\":[\"admin\",\"owner\"]", json, StringComparison.Ordinal);
        Assert.Equal(user.Id, JsonSerializer.Deserialize<UserSummary>(json, Http())!.Id);

        var request = JsonSerializer.Deserialize<IssueApiTokenRequest>("""{"name":"t","scopes":["config:read","users:write"],"expiresAt":null}""", Http())!;
        Assert.Equal([Permission.ConfigRead, Permission.UsersWrite], request.Scopes);
    }

    [Theory]
    [InlineData("""{"username":"a","password":"b","extra":1}""")]
    [InlineData("""{"username":"a"}""")]
    [InlineData("""{"username":"a","password":null}""")]
    [InlineData("""{"username":null,"password":"b"}""")]
    public void ReadingIsStrict(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>(json, Http()));

    [Fact]
    public void AddToIsIdempotent_AndAPropertyGivenTwiceIsRefused()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        IdentityJsonContext.AddTo(options);
        var converters = options.Converters.Count;
        var resolvers = options.TypeInfoResolverChain.Count;

        IdentityJsonContext.AddTo(options);

        Assert.Equal(converters, options.Converters.Count);
        Assert.Equal(resolvers, options.TypeInfoResolverChain.Count);
        Assert.False(options.AllowDuplicateProperties);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>("""{"username":"a","password":"b","password":"c"}""", options));
    }

    [Fact]
    public void AnOptionalMemberMayBeLeftOut()
    {
        var request = JsonSerializer.Deserialize<UpdateUserRequest>("""{"disabled":true}""", Http())!;

        Assert.True(request.Disabled);
        Assert.Null(request.DisplayName);
        Assert.Null(request.Roles);
    }

    [Fact]
    public void ACredentialHoldingRequestPrintsRedacted()
    {
        Assert.DoesNotContain("hunter2", new LoginRequest("alice", "hunter2").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new SetupRequest("tok", "alice", "hunter2").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tok", new SetupRequest("tok", "alice", "hunter2").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", new PasswordConfirmation("hunter2").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456", new TotpConfirmation("123456").ToString(), StringComparison.Ordinal);
    }
}
