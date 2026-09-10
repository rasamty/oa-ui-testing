using System.Globalization;
using System.Security.Cryptography;
using BuildingBlocks.Auth.Data;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Login;

/// <summary>
/// A short-lived, single-use ticket handed to the client between the two halves of
/// a login: after the password is accepted but before the OTP is. The raw ticket is
/// <c>{id}.{secret}</c>; only the SHA-256 of the secret is stored.
/// </summary>
public sealed record LoginTicket
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Purpose { get; init; }
    public required DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Persistence for login tickets. One SQLite implementation ships with the library.</summary>
public interface ILoginTicketStore
{
    /// <summary>Create a ticket and return the raw value to give the client.</summary>
    Task<string> IssueAsync(string userId, string purpose, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>Validate and consume a ticket. Returns the ticket if it was valid, unconsumed and unexpired; else null.</summary>
    Task<LoginTicket?> ConsumeAsync(string rawTicket, string expectedPurpose, CancellationToken ct = default);
}

/// <summary>SQLite-backed <see cref="ILoginTicketStore"/> (<c>auth_login_tickets</c>).</summary>
public sealed class SqliteLoginTicketStore : ILoginTicketStore
{
    private const string Iso = "o";
    private readonly AuthDatabase _db;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteLoginTicketStore(AuthDatabase db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> IssueAsync(string userId, string purpose, TimeSpan lifetime, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("n");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var raw = $"{id}.{secret}";

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO auth_login_tickets (id, ticket_hash, user_id, purpose, created_utc, expires_utc)
                VALUES ($id, $hash, $uid, $p, $created, $expires);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$hash", Hash(secret));
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$p", purpose);
            cmd.Parameters.AddWithValue("$created", now.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", now.Add(lifetime).ToString(Iso, CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }

        return raw;
    }

    public async Task<LoginTicket?> ConsumeAsync(string rawTicket, string expectedPurpose, CancellationToken ct = default)
    {
        var parts = rawTicket.Split('.', 2);
        if (parts.Length != 2) return null;
        var (id, secret) = (parts[0], parts[1]);
        var now = _clock.GetUtcNow();

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);

            LoginTicket? ticket = null;
            await using (var read = con.CreateCommand())
            {
                read.CommandText =
                    """
                    SELECT user_id, purpose, expires_utc, consumed_utc, ticket_hash
                    FROM auth_login_tickets WHERE id = $id;
                    """;
                read.Parameters.AddWithValue("$id", id);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;

                var userId = r.GetString(0);
                var purpose = r.GetString(1);
                var expires = ParseUtc(r.GetString(2));
                var consumed = !r.IsDBNull(3);
                var storedHash = r.GetString(4);

                if (consumed || now >= expires || purpose != expectedPurpose) return null;
                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(Hash(secret)),
                        System.Text.Encoding.UTF8.GetBytes(storedHash)))
                    return null;

                ticket = new LoginTicket { Id = id, UserId = userId, Purpose = purpose, ExpiresUtc = expires };
            }

            await using (var consume = con.CreateCommand())
            {
                consume.CommandText =
                    "UPDATE auth_login_tickets SET consumed_utc = $t WHERE id = $id AND consumed_utc IS NULL;";
                consume.Parameters.AddWithValue("$t", now.ToString(Iso, CultureInfo.InvariantCulture));
                consume.Parameters.AddWithValue("$id", id);
                var rows = await consume.ExecuteNonQueryAsync(ct);
                return rows > 0 ? ticket : null; // lost a race to consume
            }
        }
        finally { _writeLock.Release(); }
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes);
    }

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
