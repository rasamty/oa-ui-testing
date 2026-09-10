namespace BuildingBlocks.Auth;

/// <summary>
/// Every knob for the auth layer, bound from the "Auth" configuration section.
/// The one thing that is NOT here is the JWT signing key — that is a secret and
/// comes from <c>AUTH_SIGNING_KEY</c> (environment / Key Vault), never appsettings.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Goes into the <c>iss</c> claim and is checked on validation. e.g. "https://align.repriori.com".</summary>
    public string Issuer { get; set; } = "repriori";

    /// <summary>Goes into the <c>aud</c> claim and is checked on validation.</summary>
    public string Audience { get; set; } = "repriori-apis";

    /// <summary>How long an access token is usable. Short on purpose — the refresh token covers the gap.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>How long a refresh token lives. It rotates on every use.</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// When a revoked refresh token is replayed, also revoke every other session for
    /// that user (treat it as a stolen token). Off by default: a client that fires
    /// two refreshes at once would otherwise log itself out. Turn on where the extra
    /// theft protection is worth that risk.
    /// </summary>
    public bool RevokeAllOnRefreshReuse { get; set; } = false;

    /// <summary>New accounts get a trial this many days long unless the admin sets explicit dates.</summary>
    public int DefaultTrialDays { get; set; } = 14;

    /// <summary>Require a TOTP code at login. Off by default; turn on per deployment.</summary>
    public bool RequireTwoFactor { get; set; } = false;

    /// <summary>Failed login attempts before a username is locked.</summary>
    public int LockoutAttempts { get; set; } = 8;

    /// <summary>How long a lockout lasts.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Shown in the authenticator app when a user enrols 2FA.</summary>
    public string TotpIssuerName { get; set; } = "Repriori";

    /// <summary>The cookie name for the refresh token (scoped to the refresh endpoint only).</summary>
    public string RefreshCookieName { get; set; } = "align_rt";

    /// <summary>
    /// Force the <c>Secure</c> flag on the refresh cookie. Null (default) = follow the request
    /// scheme. Set true on a deployment that terminates TLS at a proxy and talks to the app over http.
    /// </summary>
    public bool? RefreshCookieSecure { get; set; }

    /// <summary>
    /// Set on a deployment reached through a proxy on a different hostname than the app sees
    /// (Cloudflare in front of *.azurewebsites.net). Pins the refresh cookie to this domain.
    /// </summary>
    public string? CookieDomain { get; set; }

    public PasswordPolicy Password { get; set; } = new();
    public UsernamePolicy Username { get; set; } = new();

    /// <summary>Clock skew allowed when validating a token's lifetime.</summary>
    public int ClockSkewSeconds { get; set; } = 30;

    public sealed class PasswordPolicy
    {
        public int MinLength { get; set; } = 12;
        public bool MustDifferFromUsername { get; set; } = true;
    }

    public sealed class UsernamePolicy
    {
        public int MinLength { get; set; } = 3;
        public int MaxLength { get; set; } = 32;
        public string Pattern { get; set; } = "^[a-zA-Z0-9._-]+$";
    }
}
