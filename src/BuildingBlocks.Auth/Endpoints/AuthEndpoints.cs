using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Endpoints;

/// <summary>
/// Maps the token endpoints. A host calls <c>app.MapBuildingBlocksAuth()</c> and
/// gets <c>POST /auth/login</c>, <c>/auth/login/2fa</c>, <c>/auth/refresh</c>,
/// <c>/auth/logout</c> and <c>GET /auth/me</c>. Nothing here is product-specific.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapBuildingBlocksAuth(this IEndpointRouteBuilder app, string prefix = "/auth")
    {
        var group = app.MapGroup(prefix);

        group.MapPost("/login", async (LoginBody body, LoginService login, HttpContext http,
            IOptions<AuthOptions> opts) =>
        {
            var result = await login.PasswordLoginAsync(body.Username, body.Password, http.RequestAborted);
            return Respond(result, http, opts.Value, prefix);
        });

        group.MapPost("/login/2fa", async (TwoFactorBody body, LoginService login, HttpContext http,
            IOptions<AuthOptions> opts) =>
        {
            var result = await login.CompleteTwoFactorAsync(body.Ticket, body.Code, http.RequestAborted);
            return Respond(result, http, opts.Value, prefix);
        });

        group.MapPost("/refresh", async (HttpContext http, RefreshTokenService refresh, IUserStore users,
            AccessTokenService access, IOptions<AuthOptions> opts) =>
        {
            var raw = http.Request.Cookies[opts.Value.RefreshCookieName]
                      ?? await ReadBodyRefreshToken(http);
            if (string.IsNullOrEmpty(raw))
                return Results.Json(new { detail = "No refresh token." }, statusCode: StatusCodes.Status401Unauthorized);

            var rotate = await refresh.RotateAsync(raw, http.RequestAborted);
            if (!rotate.Ok || rotate.UserId is null)
            {
                ClearRefreshCookie(http, opts.Value, prefix);
                return Results.Json(new { detail = $"Refresh rejected ({rotate.Outcome})." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var user = await users.FindByIdAsync(rotate.UserId, http.RequestAborted);
            if (user is null || !user.IsActive || !user.IsWithinTrial(DateTimeOffset.UtcNow))
            {
                await refresh.RevokeAllAsync(rotate.UserId, http.RequestAborted);
                ClearRefreshCookie(http, opts.Value, prefix);
                return Results.Json(new { detail = "Account is not usable." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, exp) = access.Issue(user, amr: "refresh");
            SetRefreshCookie(http, opts.Value, prefix, rotate.Next!);
            return Results.Ok(new TokenResponse(token, exp, "Bearer",
                rotate.Next!.Raw, rotate.Next.ExpiresUtc, user.MustChangePassword, ToMe(user)));
        });

        group.MapPost("/logout", async (HttpContext http, RefreshTokenService refresh, IOptions<AuthOptions> opts) =>
        {
            var raw = http.Request.Cookies[opts.Value.RefreshCookieName];
            if (!string.IsNullOrEmpty(raw))
                await refresh.RevokeAsync(raw, http.RequestAborted);
            ClearRefreshCookie(http, opts.Value, prefix);
            return Results.NoContent();
        });

        group.MapGet("/me", async (HttpContext http, IUserStore users) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (sub is null) return Results.Unauthorized();
            var user = await users.FindByIdAsync(sub, http.RequestAborted);
            return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user));
        }).RequireAuthorization();

        return app;
    }

    private static IResult Respond(LoginResult result, HttpContext http, AuthOptions opts, string prefix)
    {
        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                SetRefreshCookie(http, opts, prefix, result.RefreshToken!);
                return Results.Ok(new TokenResponse(
                    result.AccessToken!, result.AccessExpiresUtc!.Value, "Bearer",
                    result.RefreshToken!.Raw, result.RefreshToken.ExpiresUtc,
                    result.MustChangePassword, ToMe(result.User!)));

            case LoginOutcome.TwoFactorRequired:
                return Results.Ok(new TwoFactorRequiredResponse(result.TwoFactorTicket!));

            case LoginOutcome.LockedOut:
                return Results.Json(new { detail = "Too many attempts. Try again later.", lockedOutUntil = result.LockedOutUntil },
                    statusCode: StatusCodes.Status429TooManyRequests);

            case LoginOutcome.Disabled:
                return Results.Json(new { detail = "This account is disabled." },
                    statusCode: StatusCodes.Status403Forbidden);

            case LoginOutcome.OutsideAccessWindow:
                return Results.Json(new { detail = "This account's access window has ended." },
                    statusCode: StatusCodes.Status403Forbidden);

            default:
                return Results.Json(new { detail = "Invalid username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<string?> ReadBodyRefreshToken(HttpContext http)
    {
        if (!http.Request.HasJsonContentType()) return null;
        try
        {
            var body = await http.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            return body is not null && body.TryGetValue("refreshToken", out var t) ? t : null;
        }
        catch { return null; }
    }

    private static void SetRefreshCookie(HttpContext http, AuthOptions opts, string prefix, IssuedRefreshToken token)
    {
        http.Response.Cookies.Append(opts.RefreshCookieName, token.Raw, new CookieOptions
        {
            HttpOnly = true,
            Secure = opts.RefreshCookieSecure ?? http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = $"{prefix}",
            Domain = string.IsNullOrWhiteSpace(opts.CookieDomain) ? null : opts.CookieDomain,
            Expires = token.ExpiresUtc,
        });
    }

    private static void ClearRefreshCookie(HttpContext http, AuthOptions opts, string prefix)
    {
        http.Response.Cookies.Delete(opts.RefreshCookieName, new CookieOptions
        {
            Path = $"{prefix}",
            Domain = string.IsNullOrWhiteSpace(opts.CookieDomain) ? null : opts.CookieDomain,
        });
    }

    private static MeResponse ToMe(AuthUser u) => new(
        u.Id, u.Username, u.OrganisationId, u.Role, u.PermissionList(), u.TwoFactorEnabled, u.AccessEndsUtc);
}
