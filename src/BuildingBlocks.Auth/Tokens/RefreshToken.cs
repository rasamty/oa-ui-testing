namespace BuildingBlocks.Auth.Tokens;

/// <summary>
/// A stored refresh token. The raw secret is never persisted — only
/// <see cref="TokenHash"/> (SHA-256). Tokens rotate: using one marks it revoked and
/// records the id of its replacement in <see cref="ReplacedById"/>.
/// </summary>
public sealed record RefreshToken
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string TokenHash { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset ExpiresUtc { get; init; }
    public DateTimeOffset? RevokedUtc { get; init; }
    public string? ReplacedById { get; init; }

    public bool IsActive(DateTimeOffset now) => RevokedUtc is null && now < ExpiresUtc;
}
