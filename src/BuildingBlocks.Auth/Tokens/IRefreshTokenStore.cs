namespace BuildingBlocks.Auth.Tokens;

/// <summary>Persistence for refresh tokens. One SQLite implementation ships with the library.</summary>
public interface IRefreshTokenStore
{
    Task AddAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>Look a token up by the SHA-256 of its raw value. Null if unknown.</summary>
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Mark a token revoked and (optionally) record which token replaced it.</summary>
    Task RevokeAsync(string id, string? replacedById, DateTimeOffset whenUtc, CancellationToken ct = default);

    /// <summary>Revoke every non-expired token for a user. Returns how many were revoked. Used by "sign out everywhere".</summary>
    Task<int> RevokeAllForUserAsync(string userId, DateTimeOffset whenUtc, CancellationToken ct = default);

    /// <summary>Delete expired / long-revoked rows. Housekeeping only.</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken ct = default);
}
