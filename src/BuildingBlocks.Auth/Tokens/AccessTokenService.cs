using System.Security.Claims;
using System.Text;
using BuildingBlocks.Auth.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Auth.Tokens;

/// <summary>The HMAC key for signing access tokens. Populated from <c>AUTH_SIGNING_KEY</c> — never appsettings.</summary>
public sealed record AuthSigningKey(byte[] Key)
{
    public static AuthSigningKey FromString(string s) => new(Encoding.UTF8.GetBytes(s));
}

/// <summary>
/// Issues and validates the short-lived access JWT. The token's claims ARE the
/// authorization data — no endpoint ever re-reads the database to decide what a
/// caller may do.
/// </summary>
public sealed class AccessTokenService
{
    private readonly AuthOptions _opts;
    private readonly SigningCredentials _creds;
    private readonly TokenValidationParameters _validation;
    private readonly JsonWebTokenHandler _handler = new();

    public AccessTokenService(IOptions<AuthOptions> opts, AuthSigningKey signingKey)
    {
        _opts = opts.Value;
        var key = new SymmetricSecurityKey(signingKey.Key);
        _creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        _validation = new TokenValidationParameters
        {
            ValidIssuer = _opts.Issuer,
            ValidAudience = _opts.Audience,
            IssuerSigningKey = key,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_opts.ClockSkewSeconds),
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
        };
    }

    public TokenValidationParameters ValidationParameters => _validation;

    /// <summary>Mint an access token for a user. <paramref name="amr"/> = how they authenticated ("pwd" or "pwd otp").</summary>
    public (string token, DateTimeOffset expiresUtc) Issue(AuthUser user, string amr)
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddMinutes(_opts.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new("sub", user.Id),
            new("name", user.Username),
            new("org", user.OrganisationId),
            new(ClaimTypes.Role, user.Role),
            new("amr", amr),
            new("jti", Guid.NewGuid().ToString("n")),
        };
        foreach (var p in user.PermissionList())
            claims.Add(new Claim("perm", p));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _opts.Issuer,
            Audience = _opts.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = exp.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = _creds,
        };
        return (_handler.CreateToken(descriptor), exp);
    }

    /// <summary>Validate the crypto and claims of a token. Returns the principal, or null if it is no good.</summary>
    public async Task<ClaimsPrincipal?> ValidateAsync(string token)
    {
        var result = await _handler.ValidateTokenAsync(token, _validation);
        return result.IsValid ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
    }
}
