using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Identity;

/// <summary>User management: reading needs <c>users:read</c> (or being the user), changing needs <c>users:write</c>.</summary>
internal static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var users = api.MapGroup("users");
        users.MapGet(string.Empty, ListAsync).RequirePermission(Permission.UsersRead).WithName("ListUsers");
        users.MapPost(string.Empty, CreateAsync).RequirePermission(Permission.UsersWrite).WithName("CreateUser");

        // A user may read themselves without users:read; the service decides, so only a caller is required here.
        users.MapGet("{id:UserId}", GetAsync).RequireAuthenticated().WithName("GetUser");
        users.MapPatch("{id:UserId}", UpdateAsync).RequirePermission(Permission.UsersWrite).WithName("UpdateUser");
        users.MapDelete("{id:UserId}", DeleteAsync).RequirePermission(Permission.UsersWrite).WithName("DeleteUser");
    }

    private static async Task<IResult> ListAsync(AdminCaller caller, HttpContext http, UserService users)
    {
        var result = await users.ListAsync(caller.Origin, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Users);
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
