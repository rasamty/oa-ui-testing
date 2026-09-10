using BuildingBlocks.Auth.Admin;
using BuildingBlocks.Auth.Authorization;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BuildingBlocks.Auth.Endpoints;

/// <summary>
/// The <c>users.admin</c> API, mounted by <see cref="AuthEndpoints.MapBuildingBlocksAuth"/>
/// under <c>&lt;prefix&gt;/admin/users</c>. Every route requires the
/// <c>perm:users.admin</c> policy (the Admin role satisfies it).
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdmin(IEndpointRouteBuilder group)
    {
        var admin = group.MapGroup("/admin/users")
            .RequireAuthorization(AuthPolicies.Policy(AuthPolicies.UsersAdmin));

        admin.MapGet("", async (HttpContext http, AdminService svc, string? org) =>
        {
            var users = await svc.ListAsync(org, http.RequestAborted);
            return Results.Ok(users.Select(View));
        });

        admin.MapGet("/{id}", async (string id, HttpContext http, AdminService svc) =>
        {
            var user = await svc.GetAsync(id, http.RequestAborted);
            return user is null ? Results.NotFound() : Results.Ok(View(user));
        });

        admin.MapPost("", async (AdminCreateUserBody body, HttpContext http, AdminService svc) =>
        {
            var actingOrg = http.User.FindFirst("org")?.Value ?? "demo";
            var (outcome, error, user) = await svc.CreateAsync(new NewUserRequest
            {
                Username = body.Username,
                Password = body.Password,
                OrganisationId = string.IsNullOrWhiteSpace(body.OrganisationId) ? actingOrg : body.OrganisationId!,
                Role = string.IsNullOrWhiteSpace(body.Role) ? "Member" : body.Role!,
                Permissions = body.Permissions,
                MustChangePassword = body.MustChangePassword,
                AccessEndsUtc = body.TrialDays is { } d ? DateTimeOffset.UtcNow.AddDays(d) : null,
            }, http.RequestAborted);

            return outcome == AdminOutcome.Ok
                ? Results.Created($"/admin/users/{user!.Id}", View(user))
                : Results.Json(new { detail = error }, statusCode: StatusCodes.Status400BadRequest);
        });

        admin.MapPost("/{id}/disable", (string id, HttpContext http, AdminService svc) =>
            RunActive(id, http, svc, active: false));

        admin.MapPost("/{id}/enable", (string id, HttpContext http, AdminService svc) =>
            RunActive(id, http, svc, active: true));

        admin.MapPost("/{id}/reset-password", async (string id, AdminResetPasswordBody body,
            HttpContext http, AdminService svc) =>
            Respond(await svc.ResetPasswordAsync(id, body.NewPassword, body.MustChange, http.RequestAborted)));

        admin.MapPost("/{id}/reset-2fa", async (string id, HttpContext http, AdminService svc) =>
            Respond(await svc.ResetTwoFactorAsync(id, http.RequestAborted)));

        admin.MapPost("/{id}/access-window", async (string id, AdminAccessWindowBody body,
            HttpContext http, AdminService svc) =>
            Respond(await svc.SetAccessWindowAsync(id, body.StartsUtc, body.EndsUtc, http.RequestAborted)));

        admin.MapPost("/{id}/extend-trial", async (string id, AdminExtendTrialBody body,
            HttpContext http, AdminService svc) =>
            Respond(await svc.ExtendTrialAsync(id, body.Days, http.RequestAborted)));

        admin.MapPost("/{id}/revoke-sessions", async (string id, HttpContext http, AdminService svc) =>
            Respond(await svc.RevokeSessionsAsync(id, http.RequestAborted)));

        admin.MapDelete("/{id}", async (string id, HttpContext http, AdminService svc) =>
        {
            var acting = http.User.FindFirst("sub")?.Value ?? "";
            var outcome = await svc.DeleteAsync(acting, id, http.RequestAborted);
            return outcome switch
            {
                AdminOutcome.Ok => Results.NoContent(),
                AdminOutcome.NotFound => Results.NotFound(),
                AdminOutcome.NotAllowed => Results.Json(new { detail = "You cannot delete your own account." },
                    statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Json(new { detail = "Could not delete the account." },
                    statusCode: StatusCodes.Status400BadRequest),
            };
        });
    }

    private static async Task<IResult> RunActive(string id, HttpContext http, AdminService svc, bool active)
    {
        var acting = http.User.FindFirst("sub")?.Value ?? "";
        return Respond(await svc.SetActiveAsync(acting, id, active, http.RequestAborted));
    }

    private static IResult Respond(AdminResult r) => r.Outcome switch
    {
        AdminOutcome.Ok => Results.Ok(View(r.User!)),
        AdminOutcome.NotFound => Results.NotFound(),
        AdminOutcome.NotAllowed => Results.Json(new { detail = r.Error }, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new { detail = r.Error }, statusCode: StatusCodes.Status400BadRequest),
    };

    private static AdminUserView View(AuthUser u) => new(
        u.Id, u.Username, u.OrganisationId, u.Role, u.PermissionList(),
        u.IsActive, u.MustChangePassword, u.TwoFactorEnabled,
        u.AccessStartsUtc, u.AccessEndsUtc, u.CreatedUtc, u.LastLoginUtc);
}
