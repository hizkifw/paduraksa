using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Identity;

/// <summary>User management: reading needs <c>users:read</c> (or being the user), changing needs <c>users:write</c>.</summary>
internal static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("users");
        users.MapGet(string.Empty, ListAsync).RequirePermission(Permission.UsersRead).WithName("ListUsers").WithSummary("List users")
            .Produces<Page<UserSummary>>().ProducesApiProblems(400, 403);
        users.MapPost(string.Empty, CreateAsync).RequirePermission(Permission.UsersWrite).WithName("CreateUser").WithSummary("Create a user")
            .Accepts<CreateUserRequest>("application/json").Produces<UserSummary>(StatusCodes.Status201Created).ProducesApiProblems(400, 403, 409, 415, 422);

        // A user may read themselves without users:read; the service decides, so only a caller is required here.
        users.MapGet("{id:UserId}", GetAsync).RequireAuthenticated().WithName("GetUser").WithSummary("Get a user")
            .WithDescription("A user may read themselves; reading another needs users:read.")
            .Produces<UserSummary>().ProducesApiProblems(403, 404);
        users.MapPatch("{id:UserId}", UpdateAsync).RequirePermission(Permission.UsersWrite).WithName("UpdateUser").WithSummary("Change a user")
            .WithDescription("A partial update: the display name, roles and disabled flag that are given. Users carry no version, so there is no If-Match: the last write wins.")
            .Accepts<UpdateUserRequest>("application/json").Produces<UserSummary>().ProducesApiProblems(400, 403, 404, 409, 415, 422);
        users.MapDelete("{id:UserId}", DeleteAsync).RequirePermission(Permission.UsersWrite).WithName("DeleteUser").WithSummary("Delete a user")
            .Produces(StatusCodes.Status204NoContent).ProducesApiProblems(403, 404, 409);
    }

    private static async Task<IResult> ListAsync(AdminCaller caller, HttpContext http, UserService users)
    {
        var result = await users.ListAsync(caller.Origin, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user")
            ?? (Pagination.TryPage(http, result.Users, u => u.Id, out var page, out var problem) ? Results.Ok(page) : problem!);
    }

    private static async Task<IResult> GetAsync(UserId id, AdminCaller caller, HttpContext http, UserService users)
    {
        var result = await users.GetAsync(caller.Origin, id, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.Ok(result.User);
    }

    private static async Task<IResult> CreateAsync(CreateUserRequest request, AdminCaller caller, HttpContext http, UserService users)
    {
        var result = await users.CreateAsync(caller.Origin, request, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.Created($"/api/v1/users/{result.User!.Id}", result.User);
    }

    /// <summary>Changes the display name, roles and enabled state in one service call: all of it applies, or none.</summary>
    private static async Task<IResult> UpdateAsync(UserId id, UpdateUserRequest request, AdminCaller caller, HttpContext http, UserService users)
    {
        if (request.DisplayName is null && request.Roles is null && request.Disabled is null)
        {
            return AdminProblems.Invalid("Nothing to change: give a displayName, roles or disabled.");
        }

        var result = await users.UpdateAsync(caller.Origin, id, request, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.Ok(result.User);
    }

    private static async Task<IResult> DeleteAsync(UserId id, AdminCaller caller, HttpContext http, UserService users)
    {
        var result = await users.DeleteAsync(caller.Origin, id, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.NoContent();
    }
}
