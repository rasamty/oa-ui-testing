using System.Collections.Concurrent;
using System.Globalization;
using BuildingBlocks.Auth.Data;
using Microsoft.Data.Sqlite;

namespace BuildingBlocks.Auth.Tokens;

/// <summary>
/// A per-user cut-off instant. Any access token for that user issued <em>before</em>
/// the cut-off is rejected, even though its signature and lifetime are still valid.
/// This is how "disable this account" or "sign this user out everywhere" takes
/// effect on the very next request instead of waiting for the token to expire.
/// </summary>
public interface ITokenDenylist
{
    /// <summary>Reject every token for <paramref name="userId"/> issued before <paramref name="cutoffUtc"/>.</summary>
    Task DenyBeforeAsync(string userId, DateTimeOffset cutoffUtc, CancellationToken ct = default);

    /// <summary>Lift the cut-off (e.g. when re-enabling an account). Safe if there is none.</summary>
    Task ClearAsync(string userId, CancellationToken ct = default);

    /// <summary>True if a token for <paramref name="userId"/> issued at <paramref name="issuedAtUtc"/> is now denied.</summary>
    Task<bool> IsDeniedAsync(string userId, DateTimeOffset issuedAtUtc, CancellationToken ct = default);
}

/// <summary>
/// SQLite-backed <see cref="ITokenDenylist"/> (<c>auth_token_denylist</c>) with a
/// small in-process snapshot so the hot path (every authenticated request) is not
/// a database round-trip. The table only ever holds a row per disabled / revoked
/// user, so the snapshot is tiny; it is reloaded when older than
/// <see cref="RefreshInterval"/> or right after this process writes to it.
/// </summary>
public sealed class SqliteTokenDenylist : ITokenDenylist
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly AuthDatabase _db;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile ConcurrentDictionary<string, DateTimeOffset> _snapshot = new();
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public SqliteTokenDenylist(AuthDatabase db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task DenyBeforeAsync(string userId, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO auth_token_denylist (user_id, since_utc) VALUES ($uid, $t)
                ON CONFLICT(user_id) DO UPDATE SET since_utc = excluded.since_utc;
                """;
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$t", cutoffUtc.ToString("o", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }

        _snapshot[userId] = cutoffUtc;   // apply locally at once
        _loadedAt = _clock.GetUtcNow();
    }

    public async Task ClearAsync(string userId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM auth_token_denylist WHERE user_id = $uid;";
            cmd.Parameters.AddWithValue("$uid", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }

        _snapshot.TryRemove(userId, out _);
    }

    public async Task<bool> IsDeniedAsync(string userId, DateTimeOffset issuedAtUtc, CancellationToken ct = default)
    {
        var snap = await CurrentSnapshotAsync(ct);
        // A token is denied if it was issued at or before the cut-off. The 1s slack
        // absorbs the whole-second resolution of the JWT `iat` claim.
        return snap.TryGetValue(userId, out var cutoff) && issuedAtUtc <= cutoff.AddSeconds(1);
    }

    private async Task<ConcurrentDictionary<string, DateTimeOffset>> CurrentSnapshotAsync(CancellationToken ct)
    {
        if (_clock.GetUtcNow() - _loadedAt < RefreshInterval)
            return _snapshot;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_clock.GetUtcNow() - _loadedAt < RefreshInterval)
                return _snapshot;

            var fresh = new ConcurrentDictionary<string, DateTimeOffset>();
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT user_id, since_utc FROM auth_token_denylist;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                fresh[r.GetString(0)] = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            _snapshot = fresh;
            _loadedAt = _clock.GetUtcNow();
            return fresh;
        }
        finally { _loadLock.Release(); }
    }
}
