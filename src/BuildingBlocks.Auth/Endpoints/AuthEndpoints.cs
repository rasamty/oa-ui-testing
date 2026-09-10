using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Totp;
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
                // Don't clear the cookie here: a client that fired two refreshes at
                // once would have one of them lose the race and must not lose its
                // still-valid session. Only /logout clears the cookie.
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

        // Change your own password. Clears MustChangePassword, drops every other session,
        // and hands back a fresh token pair so the caller stays signed in here.
        group.MapPost("/change-password", async (ChangePasswordBody body, HttpContext http,
            PasswordChangeService svc, IOptions<AuthOptions> opts) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (sub is null) return Results.Unauthorized();

            var r = await svc.ChangeAsync(sub, body.CurrentPassword, body.NewPassword, http.RequestAborted);
            if (!r.Ok)
                return r.Outcome switch
                {
                    PasswordChangeOutcome.WrongCurrentPassword or PasswordChangeOutcome.PolicyViolation
                        => Results.Json(new { detail = r.Error }, statusCode: StatusCodes.Status400BadRequest),
                    _ => Results.Unauthorized(),
                };

            SetRefreshCookie(http, opts.Value, prefix, r.RefreshToken!);
            return Results.Ok(new TokenResponse(
                r.AccessToken!, r.AccessExpiresUtc!.Value, "Bearer",
                r.RefreshToken!.Raw, r.RefreshToken.ExpiresUtc, false, ToMe(r.User!)));
        }).RequireAuthorization();

        // Change your own username. Needs the password. Returns a fresh token pair
        // (the id / sub is unchanged, only the display name).
        group.MapPost("/change-username", async (ChangeUsernameBody body, HttpContext http,
            UsernameChangeService svc, IOptions<AuthOptions> opts) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (sub is null) return Results.Unauthorized();

            var r = await svc.ChangeAsync(sub, body.NewUsername, body.CurrentPassword, http.RequestAborted);
            if (!r.Ok)
                return r.Outcome switch
                {
                    UsernameChangeOutcome.WrongPassword or UsernameChangeOutcome.PolicyViolation
                        or UsernameChangeOutcome.Taken or UsernameChangeOutcome.Unchanged
                        => Results.Json(new { detail = r.Error }, statusCode: StatusCodes.Status400BadRequest),
                    _ => Results.Unauthorized(),
                };

            SetRefreshCookie(http, opts.Value, prefix, r.RefreshToken!);
            return Results.Ok(new TokenResponse(
                r.AccessToken!, r.AccessExpiresUtc!.Value, "Bearer",
                r.RefreshToken!.Raw, r.RefreshToken.ExpiresUtc, r.User!.MustChangePassword, ToMe(r.User!)));
        }).RequireAuthorization();

        // ---- two-factor enrolment ----

        group.MapPost("/2fa/setup", async (HttpContext http, TwoFactorService twoFactor) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (sub is null) return Results.Unauthorized();
            var setup = await twoFactor.BeginSetupAsync(sub, http.RequestAborted);
            return setup is null
                ? Results.Unauthorized()
                : Results.Ok(new TwoFactorSetupResponse(setup.Secret, setup.OtpAuthUri, setup.AlreadyEnabled));
        }).RequireAuthorization();

        group.MapPost("/2fa/confirm", async (ConfirmTwoFactorBody body, HttpContext http, TwoFactorService twoFactor) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (sub is null) return Results.Unauthorized();
            var r = await twoFactor.ConfirmAsync(sub, body.Code, http.RequestAborted);
            return r.Outcome switch
            {
                TwoFactorConfirmOutcome.Enabled =>
                    Results.Ok(new RecoveryCodesResponse(r.RecoveryCodes ?? Array.Empty<string>())),
                TwoFactorConfirmOutcome.WrongCode =>
                    Results.Json(new { detail = "That code is not right — check your authenticator and try again." },
                        statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Json(new { detail = "Start the setup first." },
                        statusCode: StatusCodes.Status400BadRequest),
            };
        }).RequireAuthorization();

        group.MapPost("/2fa/disable", async (DisableTwoFactorBody body, HttpContext http, TwoFactorService twoFactor) =>
        {
            var sub = http.User.FindFirst("sub")?.Value;
            if (sub is null) return Results.Unauthorized();
            var outcome = await twoFactor.DisableAsync(sub, body.CurrentPassword, http.RequestAborted);
            return outcome switch
            {
                TwoFactorDisableOutcome.Disabled => Results.NoContent(),
                TwoFactorDisableOutcome.WrongPassword =>
                    Results.Json(new { detail = "Password is wrong." }, statusCode: StatusCodes.Status400BadRequest),
                TwoFactorDisableOutcome.NotEnabled =>
                    Results.Json(new { detail = "Two-factor is not on for this account." },
                        statusCode: StatusCodes.Status400BadRequest),
                _ => Results.Unauthorized(),
            };
        }).RequireAuthorization();

        // ---- admin API (users.admin) ----
        AdminEndpoints.MapAdmin(group);

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
