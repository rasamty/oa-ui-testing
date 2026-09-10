namespace BuildingBlocks.Auth.Users;

/// <summary>
/// A sign-in account. Mirrors the <c>auth_users</c> table. The auth library owns
/// this shape entirely; a product never adds columns here — it puts product data
/// in its own tables keyed by <see cref="Id"/>.
/// </summary>
public sealed record AuthUser
{
    /// <summary>Stable id (a GUID). Never changes, even after a username change. This is the token's <c>sub</c>.</summary>
    public required string Id { get; init; }

    public required string Username { get; init; }

    /// <summary>PBKDF2 output. Never the raw password.</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Which tenant / workspace this user belongs to. The product API trusts THIS, never a value from a request body.</summary>
    public required string OrganisationId { get; init; }

    /// <summary>e.g. "Admin" or "Member". Drives role-based policies.</summary>
    public string Role { get; init; } = "Member";

    /// <summary>Space-separated permission strings baked into the access token's <c>perm</c> claim.</summary>
    public string Permissions { get; init; } = "";

    public bool IsActive { get; init; } = true;
    public bool MustChangePassword { get; init; }

    public bool TwoFactorEnabled { get; init; }

    /// <summary>The TOTP secret, data-protected. Null until the user enrols. As sensitive as the password hash.</summary>
    public string? TotpSecretProtected { get; init; }

    /// <summary>The trial window. Checked on every login and every API call.</summary>
    public DateTimeOffset AccessStartsUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AccessEndsUtc { get; init; } = DateTimeOffset.UtcNow.AddYears(100);

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginUtc { get; init; }

    /// <summary>Now is inside [start, end) and the account is active.</summary>
    public bool IsWithinTrial(DateTimeOffset now) => IsActive && now >= AccessStartsUtc && now < AccessEndsUtc;

    public IReadOnlyList<string> PermissionList() =>
        Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
