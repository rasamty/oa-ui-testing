using System.Text.Encodings.Web;
using BuildingBlocks.Auth.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace BuildingBlocks.Auth.Authentication;

/// <summary>Options for <see cref="BearerAuthenticationHandler"/>. Nothing to configure today — a hook for later.</summary>
public sealed class BearerAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Validates a <c>Authorization: Bearer &lt;jwt&gt;</c> header against
/// <see cref="AccessTokenService"/>. This is the whole bearer story — no
/// <c>Microsoft.AspNetCore.Authentication.JwtBearer</c> package, because the
/// library already owns token validation.
/// </summary>
/// <remarks>
/// Crypto and claims, plus the token denylist: an admin action (disable, reset
/// password, revoke sessions) sets a per-user cut-off, and any token issued before
/// it is rejected here on the very next request — no waiting for it to expire.
/// </remarks>
public sealed class BearerAuthenticationHandler : AuthenticationHandler<BearerAuthenticationOptions>
{
    public const string SchemeName = "BuildingBlocksBearer";

    private readonly AccessTokenService _tokens;
    private readonly ITokenDenylist _denylist;

    public BearerAuthenticationHandler(
        IOptionsMonitor<BearerAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccessTokenService tokens,
        ITokenDenylist denylist)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _denylist = denylist;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
            return AuthenticateResult.NoResult();

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
            return AuthenticateResult.NoResult();

        var principal = await _tokens.ValidateAsync(token);
        if (principal is null)
            return AuthenticateResult.Fail("The access token is missing, expired or invalid.");

        var sub = principal.FindFirst("sub")?.Value;
        var iat = principal.FindFirst("iat")?.Value;
        if (sub is not null && long.TryParse(iat, out var iatUnix))
        {
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatUnix);
            if (await _denylist.IsDeniedAsync(sub, issuedAt))
                return AuthenticateResult.Fail("This session has been revoked. Sign in again.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // An API: say 401 with a WWW-Authenticate hint, never redirect to a login page.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
