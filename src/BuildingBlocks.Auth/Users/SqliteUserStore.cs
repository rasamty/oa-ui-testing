using System.Globalization;
using BuildingBlocks.Auth.Data;
using Microsoft.Data.Sqlite;

namespace BuildingBlocks.Auth.Users;

/// <summary>
/// SQLite-backed <see cref="IUserStore"/>. Writes are serialised through a single
/// <see cref="SemaphoreSlim"/> — SQLite allows one writer at a time and this turns a
/// would-be "database is locked" into a short wait.
/// </summary>
public sealed class SqliteUserStore : IUserStore
{
    private const string Iso = "o";
    private readonly AuthDatabase _db;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteUserStore(AuthDatabase db) => _db = db;

    public Task EnsureSchemaAsync(CancellationToken ct = default) => _db.EnsureSchemaAsync(ct);

    public async Task<AuthUser?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var con = await _db.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM auth_users WHERE username_lower = $ul;";
        cmd.Parameters.AddWithValue("$ul", username.ToLowerInvariant());
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<AuthUser?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        await using var con = await _db.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM auth_users WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<AuthUser>> ListAsync(string? organisationId = null, CancellationToken ct = default)
    {
        await using var con = await _db.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = organisationId is null
            ? $"SELECT {Columns} FROM auth_users ORDER BY username_lower;"
            : $"SELECT {Columns} FROM auth_users WHERE organisation_id = $org ORDER BY username_lower;";
        if (organisationId is not null)
            cmd.Parameters.AddWithValue("$org", organisationId);

        var list = new List<AuthUser>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(Map(r));
        return list;
    }

    public async Task<UserCreateResult> CreateAsync(AuthUser user, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO auth_users
                  (id, username, username_lower, password_hash, organisation_id, role, permissions,
                   is_active, must_change_password, two_factor_enabled, totp_secret_protected,
                   access_starts_utc, access_ends_utc, created_utc, last_login_utc)
                VALUES
                  ($id, $u, $ul, $ph, $org, $role, $perm,
                   $active, $mcp, $tfe, $totp,
                   $starts, $ends, $created, $last);
                """;
            Bind(cmd, user);
            cmd.Parameters.AddWithValue("$id", user.Id);
            cmd.Parameters.AddWithValue("$created", user.CreatedUtc.ToString(Iso, CultureInfo.InvariantCulture));
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
                return UserCreateResult.Created;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (username_lower UNIQUE)
            {
                return UserCreateResult.UsernameTaken;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> UpdateAsync(AuthUser user, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                UPDATE auth_users SET
                  username = $u, username_lower = $ul, password_hash = $ph, organisation_id = $org,
                  role = $role, permissions = $perm, is_active = $active, must_change_password = $mcp,
                  two_factor_enabled = $tfe, totp_secret_protected = $totp,
                  access_starts_utc = $starts, access_ends_utc = $ends, last_login_utc = $last
                WHERE id = $id;
                """;
            Bind(cmd, user);
            cmd.Parameters.AddWithValue("$id", user.Id);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM auth_users WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetLastLoginAsync(string id, DateTimeOffset whenUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await _db.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE auth_users SET last_login_utc = $t WHERE id = $id;";
            cmd.Parameters.AddWithValue("$t", whenUtc.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private const string Columns =
        "id, username, password_hash, organisation_id, role, permissions, is_active, must_change_password, " +
        "two_factor_enabled, totp_secret_protected, access_starts_utc, access_ends_utc, created_utc, last_login_utc";

    /// <summary>Bind every column except <c>id</c> and <c>created_utc</c> (both immutable after insert).</summary>
    private static void Bind(SqliteCommand cmd, AuthUser u)
    {
        cmd.Parameters.AddWithValue("$u", u.Username);
        cmd.Parameters.AddWithValue("$ul", u.Username.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$ph", u.PasswordHash);
        cmd.Parameters.AddWithValue("$org", u.OrganisationId);
        cmd.Parameters.AddWithValue("$role", u.Role);
        cmd.Parameters.AddWithValue("$perm", u.Permissions);
        cmd.Parameters.AddWithValue("$active", u.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$mcp", u.MustChangePassword ? 1 : 0);
        cmd.Parameters.AddWithValue("$tfe", u.TwoFactorEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$totp", (object?)u.TotpSecretProtected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$starts", u.AccessStartsUtc.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$ends", u.AccessEndsUtc.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$last", (object?)u.LastLoginUtc?.ToString(Iso, CultureInfo.InvariantCulture) ?? DBNull.Value);
    }

    private static async Task<AuthUser?> ReadOneAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static AuthUser Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Username = r.GetString(r.GetOrdinal("username")),
        PasswordHash = r.GetString(r.GetOrdinal("password_hash")),
        OrganisationId = r.GetString(r.GetOrdinal("organisation_id")),
        Role = r.GetString(r.GetOrdinal("role")),
        Permissions = r.GetString(r.GetOrdinal("permissions")),
        IsActive = r.GetInt64(r.GetOrdinal("is_active")) != 0,
        MustChangePassword = r.GetInt64(r.GetOrdinal("must_change_password")) != 0,
        TwoFactorEnabled = r.GetInt64(r.GetOrdinal("two_factor_enabled")) != 0,
        TotpSecretProtected = r.IsDBNull(r.GetOrdinal("totp_secret_protected"))
            ? null : r.GetString(r.GetOrdinal("totp_secret_protected")),
        AccessStartsUtc = ParseUtc(r.GetString(r.GetOrdinal("access_starts_utc"))),
        AccessEndsUtc = ParseUtc(r.GetString(r.GetOrdinal("access_ends_utc"))),
        CreatedUtc = ParseUtc(r.GetString(r.GetOrdinal("created_utc"))),
        LastLoginUtc = r.IsDBNull(r.GetOrdinal("last_login_utc"))
            ? null : ParseUtc(r.GetString(r.GetOrdinal("last_login_utc"))),
    };

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
