using System.Net;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>The users endpoints: the permission matrix per role, and the status mapping.</summary>
public sealed class UserEndpointTests
{
    private static readonly string[] Roles = ["owner", "admin", "operator", "viewer", "auditor"];

    [Fact]
    public async Task PermissionsMatrixPerRole()
    {
        await using AdminHarness h = await AdminHarness.StartAsync(new Dictionary<string, string?> { ["Paduraksa:Admin:RateLimit:PermitsPerUser"] = "1000", ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "1000" });
        string owner = await h.SetUpOwnerAsync();
        Dictionary<string, string> cookies = new() { ["owner"] = owner };
        Dictionary<string, string> ids = [];
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", owner))
        {
            ids["owner"] = me.Json.GetProperty("user").GetProperty("id").GetString()!;
        }

        foreach (string role in Roles.Where(r => r != "owner"))
        {
            ids[role] = await h.CreateUserAsync(owner, role + "-user", role);
            cookies[role] = await h.SignInAsync(role + "-user");
        }

        // role -> (may read users, may write users)
        Dictionary<string, (bool Read, bool Write)> expected = new()
        {
            ["owner"] = (true, true),
            ["admin"] = (true, false),
            ["operator"] = (false, false),
            ["viewer"] = (false, false),
            ["auditor"] = (false, false),
        };

        foreach (string role in Roles)
        {
            string cookie = cookies[role];
            (bool read, bool write) = expected[role];

            using (AdminResponse list = await h.GetAsync("/api/v1/users", cookie))
            {
                Assert.Equal(read ? HttpStatusCode.OK : HttpStatusCode.Forbidden, list.Status);
                if (read)
                {
                    Assert.True(list.Json.GetArrayLength() >= Roles.Length);
                }
            }

            // Everyone may read themselves; reading someone else needs users:read.
            using (AdminResponse self = await h.GetAsync("/api/v1/users/" + ids[role], cookie))
            {
                Assert.Equal(HttpStatusCode.OK, self.Status);
            }

            using (AdminResponse other = await h.GetAsync("/api/v1/users/" + ids["viewer"], cookie))
            {
                Assert.Equal(read || role == "viewer" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, other.Status);
            }

            using (AdminResponse create = await h.PostAsync("/api/v1/users", $$"""{"username":"made-by-{{role}}","displayName":null,"password":"correct horse battery staple","roles":["viewer"]}""", cookie))
            {
                Assert.Equal(write ? HttpStatusCode.Created : HttpStatusCode.Forbidden, create.Status);
            }

            using (AdminResponse patch = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + ids["auditor"], $$"""{"displayName":"Renamed by {{role}}"}""", cookie))
            {
                Assert.Equal(write ? HttpStatusCode.OK : HttpStatusCode.Forbidden, patch.Status);
            }

            using (AdminResponse delete = await h.SendAsync(HttpMethod.Delete, "/api/v1/users/usr_00000000000000000000zz", cookie: cookie))
            {
                Assert.Equal(write ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, delete.Status);
            }
        }
    }

    [Fact]
    public async Task UserLifecycleAndStatusMapping()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        string ownerId;
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", owner))
        {
            ownerId = me.Json.GetProperty("user").GetProperty("id").GetString()!;
        }

        string id;
        using (AdminResponse created = await h.PostAsync("/api/v1/users", $$"""{"username":"Dave","displayName":null,"password":"{{AdminHarness.Password}}","roles":["viewer","viewer","auditor"]}""", owner))
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.Equal("Dave", created.Json.GetProperty("displayName").GetString());
            Assert.Equal(["auditor", "viewer"], created.Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Order(StringComparer.Ordinal));
            Assert.EndsWith(created.Json.GetProperty("id").GetString()!, created.Header("Location") ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("hash", created.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AdminHarness.Password, created.Body, StringComparison.Ordinal);
            id = created.Json.GetProperty("id").GetString()!;
        }

        // Conflict: the name is taken (case does not matter). Invalid: a weak password, no roles.
        using (AdminResponse clash = await h.PostAsync("/api/v1/users", $$"""{"username":"dave","displayName":null,"password":"{{AdminHarness.Password}}","roles":["viewer"]}""", owner))
        {
            Assert.Equal(HttpStatusCode.Conflict, clash.Status);
        }

        using (AdminResponse weak = await h.PostAsync("/api/v1/users", """{"username":"erin","displayName":null,"password":"short","roles":["viewer"]}""", owner))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.Status);
            Assert.Contains("errors", weak.Body, StringComparison.Ordinal);
        }

        using (AdminResponse noRoles = await h.PostAsync("/api/v1/users", $$"""{"username":"erin","displayName":null,"password":"{{AdminHarness.Password}}","roles":[]}""", owner))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noRoles.Status);
        }

        using (AdminResponse update = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + id, """{"displayName":"Dave D","roles":["operator"],"disabled":false}""", owner))
        {
            Assert.Equal(HttpStatusCode.OK, update.Status);
            Assert.Equal("Dave D", update.Json.GetProperty("displayName").GetString());
            Assert.Equal(["operator"], update.Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        }

        using (AdminResponse nothing = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + id, "{}", owner))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, nothing.Status);
        }

        using (AdminResponse unknownRole = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + id, """{"roles":["emperor"]}""", owner))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownRole.Status);
        }

        using (AdminResponse unknownField = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + id, """{"displayName":"x","passwordHash":"y"}""", owner))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownField.Status);
        }

        // The last Owner cannot be disabled, demoted or deleted: 409.
        using (AdminResponse disable = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + ownerId, """{"disabled":true}""", owner))
        {
            Assert.Equal(HttpStatusCode.Conflict, disable.Status);
            Assert.Equal("identity.lastOwner", disable.Json.GetProperty("code").GetString());
        }

        using (AdminResponse demote = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + ownerId, """{"roles":["viewer"]}""", owner))
        {
            Assert.Equal(HttpStatusCode.Conflict, demote.Status);
        }

        using (AdminResponse delete = await h.SendAsync(HttpMethod.Delete, "/api/v1/users/" + ownerId, cookie: owner))
        {
            Assert.Equal(HttpStatusCode.Conflict, delete.Status);
        }

        using (AdminResponse notFound = await h.GetAsync("/api/v1/users/usr_00000000000000000000zz", owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        }

        using (AdminResponse badId = await h.GetAsync("/api/v1/users/host_whatever", owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, badId.Status);
        }

        using (AdminResponse delete = await h.SendAsync(HttpMethod.Delete, "/api/v1/users/" + id, cookie: owner))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.Status);
        }

        using (AdminResponse gone = await h.GetAsync("/api/v1/users/" + id, owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        }

        Assert.NotEmpty(await h.AuditAsync("user.create"));
    }

    [Fact]
    public async Task AnUpdateAppliesNameRolesAndEnabledStateAsAWhole()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        string ownerId;
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", owner))
        {
            ownerId = me.Json.GetProperty("user").GetProperty("id").GetString()!;
        }

        string id = await h.CreateUserAsync(owner, "dave", "viewer");
        string session = await h.SignInAsync("dave");

        using (AdminResponse update = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + id, """{"displayName":"Dave D","roles":["operator"],"disabled":true}""", owner))
        {
            Assert.Equal(HttpStatusCode.OK, update.Status);
            Assert.Equal("Dave D", update.Json.GetProperty("displayName").GetString());
            Assert.Equal(["operator"], update.Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            Assert.True(update.Json.GetProperty("disabled").GetBoolean());
        }

        using (AdminResponse ended = await h.GetAsync("/api/v1/auth/me", session))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.Status);
        }

        using (AdminResponse enable = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + id, """{"disabled":false}""", owner))
        {
            Assert.False(enable.Json.GetProperty("disabled").GetBoolean());
            Assert.Equal("Dave D", enable.Json.GetProperty("displayName").GetString());
        }

        // The last Owner: the whole update is refused, so the name is unchanged too, whichever part trips the guard.
        foreach (string body in new[]
        {
            """{"displayName":"Renamed","disabled":true}""",
            """{"displayName":"Renamed","roles":["viewer"]}""",
            """{"displayName":"Renamed","roles":["viewer"],"disabled":true}""",
        })
        {
            using AdminResponse refused = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + ownerId, body, owner);
            Assert.Equal(HttpStatusCode.Conflict, refused.Status);
            Assert.Equal("identity.lastOwner", refused.Json.GetProperty("code").GetString());
        }

        using (AdminResponse unchanged = await h.GetAsync("/api/v1/users/" + ownerId, owner))
        {
            Assert.Equal("owner", unchanged.Json.GetProperty("displayName").GetString());
            Assert.False(unchanged.Json.GetProperty("disabled").GetBoolean());
            Assert.Equal(["owner"], unchanged.Json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        }
    }

    [Fact]
    public async Task AForbiddenUserWriteIsAProblemDocument()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        await h.CreateUserAsync(owner, "viewer1", "viewer");
        string viewer = await h.SignInAsync("viewer1");

        // The endpoint's own permission check refuses before the service is asked; the service-level refusal is covered by its tests.
        using AdminResponse refused = await h.PostAsync("/api/v1/users", """{"username":"x1x","displayName":null,"password":"correct horse battery staple","roles":["viewer"]}""", viewer);

        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("Permission required", refused.Json.GetProperty("title").GetString());
    }
}
