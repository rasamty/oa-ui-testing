using System.Globalization;
using BuildingBlocks.Auth.Data;
using Microsoft.Data.Sqlite;

namespace BuildingBlocks.Auth.Tokens;

/// <summary>SQLite-backed <see cref="IRefreshTokenStore"/>. Writes serialised through a semaphore.</summary>
public sealed class SqliteRefreshTokenStore : IRefreshTokenStore
{
    private const string Iso = "o";
    private readonly AuthDatabase _db;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteRefreshTokenStore(AuthDatabase db) => _db = db;

    public async Task AddAsync(RefreshToken token, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO auth_refresh_tokens (id, user_id, token_hash, created_utc, expires_utc)
                VALUES ($id, $uid, $hash, $created, $expires);
                """;
            cmd.Parameters.AddWithValue("$id", token.Id);
            cmd.Parameters.AddWithValue("$uid", token.UserId);
            cmd.Parameters.AddWithValue("$hash", token.TokenHash);
            cmd.Parameters.AddWithValue("$created", token.CreatedUtc.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", token.ExpiresUtc.ToString(Iso, CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var con = await _db.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, user_id, token_hash, created_utc, expires_utc, revoked_utc, replaced_by
            FROM auth_refresh_tokens WHERE token_hash = $hash;
            """;
        cmd.Parameters.AddWithValue("$hash", tokenHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RefreshToken
        {
            Id = r.GetString(0),
            UserId = r.GetString(1),
            TokenHash = r.GetString(2),
            CreatedUtc = ParseUtc(r.GetString(3)),
            ExpiresUtc = ParseUtc(r.GetString(4)),
            RevokedUtc = r.IsDBNull(5) ? null : ParseUtc(r.GetString(5)),
            ReplacedById = r.IsDBNull(6) ? null : r.GetString(6),
        };
    }

    public async Task RevokeAsync(string id, string? replacedById, DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                "UPDATE auth_refresh_tokens SET revoked_utc = $t, replaced_by = $rb WHERE id = $id AND revoked_utc IS NULL;";
            cmd.Parameters.AddWithValue("$t", whenUtc.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$rb", (object?)replacedById ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> RevokeAllForUserAsync(string userId, DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                "UPDATE auth_refresh_tokens SET revoked_utc = $t WHERE user_id = $uid AND revoked_utc IS NULL;";
            cmd.Parameters.AddWithValue("$t", whenUtc.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$uid", userId);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM auth_refresh_tokens WHERE expires_utc < $t;";
            cmd.Parameters.AddWithValue("$t", olderThanUtc.ToString(Iso, CultureInfo.InvariantCulture));
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
