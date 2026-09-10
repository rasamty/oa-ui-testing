using System.Globalization;
using BuildingBlocks.Auth.Data;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Login;

/// <summary>
/// SQLite-backed lockout counter (<c>auth_lockouts</c>). After
/// <see cref="AuthOptions.LockoutAttempts"/> failures within the window, the key is
/// locked for <see cref="AuthOptions.LockoutMinutes"/>. A success — or the lock
/// expiring — resets it.
/// </summary>
public sealed class SqliteLoginAttemptTracker : ILoginAttemptTracker
{
    private const string Iso = "o";
    private readonly AuthDatabase _db;
    private readonly AuthOptions _opts;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteLoginAttemptTracker(AuthDatabase db, IOptions<AuthOptions> opts, TimeProvider? clock = null)
    {
        _db = db;
        _opts = opts.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<LockoutState> GetAsync(string key, CancellationToken ct = default)
    {
        await using var con = await _db.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT failed_count, lockout_until FROM auth_lockouts WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key.ToLowerInvariant());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new LockoutState(false, null, 0);

        var count = (int)r.GetInt64(0);
        var until = r.IsDBNull(1) ? (DateTimeOffset?)null : ParseUtc(r.GetString(1));
        var locked = until is { } u && _clock.GetUtcNow() < u;
        return new LockoutState(locked, locked ? until : null, count);
    }

    public async Task<LockoutState> RecordFailureAsync(string key, CancellationToken ct = default)
    {
        key = key.ToLowerInvariant();
        var now = _clock.GetUtcNow();
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);

            int count;
            DateTimeOffset? firstFail;
            await using (var read = con.CreateCommand())
            {
                read.CommandText = "SELECT failed_count, first_fail_utc FROM auth_lockouts WHERE key = $k;";
                read.Parameters.AddWithValue("$k", key);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    count = (int)r.GetInt64(0);
                    firstFail = r.IsDBNull(1) ? null : ParseUtc(r.GetString(1));
                }
                else { count = 0; firstFail = null; }
            }

            // Start a fresh window if the previous one has fully elapsed.
            var windowMinutes = Math.Max(_opts.LockoutMinutes, 1);
            if (firstFail is { } f && now - f > TimeSpan.FromMinutes(windowMinutes))
            {
                count = 0;
                firstFail = null;
            }

            count++;
            firstFail ??= now;
            DateTimeOffset? until = count >= _opts.LockoutAttempts
                ? now.AddMinutes(_opts.LockoutMinutes)
                : null;

            await using (var up = con.CreateCommand())
            {
                up.CommandText =
                    """
                    INSERT INTO auth_lockouts (key, failed_count, first_fail_utc, lockout_until)
                    VALUES ($k, $c, $ff, $lu)
                    ON CONFLICT(key) DO UPDATE SET
                      failed_count = excluded.failed_count,
                      first_fail_utc = excluded.first_fail_utc,
                      lockout_until = excluded.lockout_until;
                    """;
                up.Parameters.AddWithValue("$k", key);
                up.Parameters.AddWithValue("$c", count);
                up.Parameters.AddWithValue("$ff", firstFail.Value.ToString(Iso, CultureInfo.InvariantCulture));
                up.Parameters.AddWithValue("$lu", (object?)until?.ToString(Iso, CultureInfo.InvariantCulture) ?? DBNull.Value);
                await up.ExecuteNonQueryAsync(ct);
            }

            return new LockoutState(until is not null, until, count);
        }
        finally { _writeLock.Release(); }
    }

    public async Task ResetAsync(string key, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM auth_lockouts WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key.ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
