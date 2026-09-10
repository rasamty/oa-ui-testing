using Microsoft.Data.Sqlite;

namespace BuildingBlocks.Auth.Data;

/// <summary>
/// Points the auth library at one SQLite file. This is deliberately a single file,
/// not sharded per organisation: login has to find a user by name before it knows
/// which organisation they belong to. A product's own data stays in its own
/// database(s) — this file only ever holds the <c>auth_*</c> tables.
/// </summary>
public sealed class AuthDatabaseOptions
{
    /// <summary>Path to the SQLite file. Relative paths resolve against the app base directory.</summary>
    public string Path { get; set; } = "Data/auth.db";
}

/// <summary>
/// Opens connections to the auth database and runs <see cref="AuthSchema"/> once
/// per process. Every SQLite-backed store in this library goes through here so the
/// schema-init race (parallel first-opens fighting over <c>PRAGMA journal_mode</c>)
/// is handled in exactly one place.
/// </summary>
public sealed class AuthDatabase
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _schemaReady;

    public AuthDatabase(AuthDatabaseOptions options)
        : this(BuildConnectionString(options.Path))
    {
    }

    private AuthDatabase(string connectionString) => _connectionString = connectionString;

    /// <summary>Point at a specific SQLite file (used by tests to target a temp path).</summary>
    public static AuthDatabase ForFile(string path) => new(BuildConnectionString(path));

    private static string BuildConnectionString(string path)
    {
        if (!System.IO.Path.IsPathRooted(path))
            path = System.IO.Path.Combine(AppContext.BaseDirectory, path);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var con = new SqliteConnection(_connectionString);
        await con.OpenAsync(ct);

        await using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        if (!_schemaReady)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_schemaReady)
                {
                    await using var schema = con.CreateCommand();
                    schema.CommandText = AuthSchema.Sql;
                    await schema.ExecuteNonQueryAsync(ct);
                    _schemaReady = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        return con;
    }

    /// <summary>Force the schema to exist. Call once at startup so the first real request is not slowed by it.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var _ = await OpenAsync(ct);
    }
}
