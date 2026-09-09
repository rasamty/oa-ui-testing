using Alignment.Api.Model;
using Microsoft.Data.Sqlite;

namespace Alignment.Api.Data;

/// <summary>
/// SQLite-backed <see cref="IUserRepository"/>. Always the base database file
/// (the same one <see cref="SqliteStateRepository"/> uses for the "demo" org),
/// because login looks a user up by name before their organisation is known.
/// </summary>
public sealed class SqliteUserRepository : IUserRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteUserRepository> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _schemaReady;

    public SqliteUserRepository(IConfiguration config, ILogger<SqliteUserRepository> log)
    {
        _log = log;
        var path = config["Alignment:Sqlite:DbPath"] ?? "Data/alignment.db";
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppContext.BaseDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var con = new SqliteConnection(_connectionString);
        await con.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = _schemaReady
            ? "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;"
            : "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;\n" + SqliteSchema.Sql;
        await cmd.ExecuteNonQueryAsync(ct);
        _schemaReady = true;
        return con;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var _ = await OpenAsync(ct);
    }

    public async Task<bool> CreateAsync(User user, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO users
                  (id, username, username_lower, password_hash, organisation_id,
                   is_active, must_change_password, created_utc, last_login_utc)
                VALUES
                  ($id, $u, $ul, $ph, $org, $active, $mcp, $created, $last);
                """;
            cmd.Parameters.AddWithValue("$id", user.Id);
            cmd.Parameters.AddWithValue("$u", user.Username);
            cmd.Parameters.AddWithValue("$ul", user.Username.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$ph", user.PasswordHash);
            cmd.Parameters.AddWithValue("$org", user.OrganisationId);
            cmd.Parameters.AddWithValue("$active", user.IsActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$mcp", user.MustChangePassword ? 1 : 0);
            cmd.Parameters.AddWithValue("$created", user.CreatedUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$last", (object?)user.LastLoginUtc?.ToString("o") ?? DBNull.Value);

            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (username_lower UNIQUE)
            {
                return false;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE username_lower = $ul;";
        cmd.Parameters.AddWithValue("$ul", username.ToLowerInvariant());
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task SetLastLoginAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE users SET last_login_utc = $t WHERE id = $id;";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetActiveAsync(string username, bool active, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var con = await OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE users SET is_active = $a WHERE username_lower = $ul;";
            cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
            cmd.Parameters.AddWithValue("$ul", username.ToLowerInvariant());
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<User?> ReadOneAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        return new User
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Username = r.GetString(r.GetOrdinal("username")),
            PasswordHash = r.GetString(r.GetOrdinal("password_hash")),
            OrganisationId = r.GetString(r.GetOrdinal("organisation_id")),
            IsActive = r.GetInt64(r.GetOrdinal("is_active")) != 0,
            MustChangePassword = r.GetInt64(r.GetOrdinal("must_change_password")) != 0,
            CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc")),
                null, System.Globalization.DateTimeStyles.RoundtripKind),
            LastLoginUtc = r.IsDBNull(r.GetOrdinal("last_login_utc"))
                ? null
                : DateTime.Parse(r.GetString(r.GetOrdinal("last_login_utc")),
                    null, System.Globalization.DateTimeStyles.RoundtripKind),
        };
    }
}
