using System.Security.Cryptography;
using BuildingBlocks.Auth.Data;
using Microsoft.Data.Sqlite;

namespace BuildingBlocks.Auth.Totp;

/// <summary>Persistence for one-time recovery codes (<c>auth_recovery_codes</c>).</summary>
public interface IRecoveryCodeStore
{
    /// <summary>Replace every code for a user with these (already hashed).</summary>
    Task ReplaceAllAsync(string userId, IReadOnlyList<string> codeHashes, CancellationToken ct = default);

    /// <summary>Mark a code used if it exists and is unused. Returns true if it was consumed.</summary>
    Task<bool> TryConsumeAsync(string userId, string codeHash, DateTimeOffset whenUtc, CancellationToken ct = default);

    /// <summary>How many unused codes remain.</summary>
    Task<int> RemainingAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Recovery codes — the way back in when the authenticator device is lost. Ten
/// codes, each usable once, shown to the user exactly once at generation time.
/// Only SHA-256 hashes are stored.
/// </summary>
public sealed class RecoveryCodeService
{
    private const int CodeCount = 10;
    private readonly IRecoveryCodeStore _store;
    private readonly TimeProvider _clock;

    public RecoveryCodeService(IRecoveryCodeStore store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Generate a fresh set, store the hashes, return the plaintext codes to show once.</summary>
    public async Task<IReadOnlyList<string>> GenerateAsync(string userId, CancellationToken ct = default)
    {
        var codes = new List<string>(CodeCount);
        for (var i = 0; i < CodeCount; i++)
            codes.Add(FormatCode(RandomNumberGenerator.GetBytes(10)));

        await _store.ReplaceAllAsync(userId, codes.Select(Hash).ToList(), ct);
        return codes;
    }

    /// <summary>Consume a code the user typed. Whitespace and case are ignored.</summary>
    public Task<bool> ConsumeAsync(string userId, string code, CancellationToken ct = default)
    {
        var normalised = Normalise(code);
        if (normalised.Length == 0) return Task.FromResult(false);
        return _store.TryConsumeAsync(userId, Hash(normalised), _clock.GetUtcNow(), ct);
    }

    public Task<int> RemainingAsync(string userId, CancellationToken ct = default) =>
        _store.RemainingAsync(userId, ct);

    // "abcde-fghij" — 10 chars from 10 bytes, easy to read out, no ambiguous glyphs.
    private static string FormatCode(byte[] bytes)
    {
        const string alphabet = "23456789abcdefghjkmnpqrstuvwxyz"; // no 0/1/l/i/o
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return new string(chars[..5]) + "-" + new string(chars[5..]);
    }

    private static string Normalise(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Hash(string code)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalise(code)));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>SQLite-backed <see cref="IRecoveryCodeStore"/>.</summary>
public sealed class SqliteRecoveryCodeStore : IRecoveryCodeStore
{
    private readonly AuthDatabase _db;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteRecoveryCodeStore(AuthDatabase db) => _db = db;

    public async Task ReplaceAllAsync(string userId, IReadOnlyList<string> codeHashes, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await con.BeginTransactionAsync(ct);

            await using (var del = con.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM auth_recovery_codes WHERE user_id = $uid;";
                del.Parameters.AddWithValue("$uid", userId);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var hash in codeHashes)
            {
                await using var ins = con.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO auth_recovery_codes (user_id, code_hash) VALUES ($uid, $h);";
                ins.Parameters.AddWithValue("$uid", userId);
                ins.Parameters.AddWithValue("$h", hash);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> TryConsumeAsync(string userId, string codeHash, DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                UPDATE auth_recovery_codes SET used_utc = $t
                WHERE user_id = $uid AND code_hash = $h AND used_utc IS NULL;
                """;
            cmd.Parameters.AddWithValue("$t", whenUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$h", codeHash);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> RemainingAsync(string userId, CancellationToken ct = default)
    {
        await using var con = await _db.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM auth_recovery_codes WHERE user_id = $uid AND used_utc IS NULL;";
        cmd.Parameters.AddWithValue("$uid", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
