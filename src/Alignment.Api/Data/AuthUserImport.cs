using BuildingBlocks.Auth.Users;
using Microsoft.Data.Sqlite;

namespace Alignment.Api.Data;

/// <summary>
/// One-time carry-over from the Phase 2 <c>users</c> table to the Phase 3
/// <c>auth_users</c> table. Runs at startup; does nothing once <c>auth_users</c>
/// has rows, so it is safe to leave in place.
/// </summary>
public static class AuthUserImport
{
    public static async Task CarryOverAsync(string dbPath, IUserStore target, ILogger logger, CancellationToken ct = default)
    {
        var resolved = Path.IsPathRooted(dbPath) ? dbPath : Path.Combine(AppContext.BaseDirectory, dbPath);
        if (!File.Exists(resolved)) return;

        // Already migrated? (any auth_users row) — bail.
        if ((await target.ListAsync(ct: ct)).Count > 0) return;

        var cs = new SqliteConnectionStringBuilder { DataSource = resolved, Pooling = true }.ToString();
        await using var con = new SqliteConnection(cs);
        await con.OpenAsync(ct);

        // Is there an old users table at all?
        await using (var check = con.CreateCommand())
        {
            check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='users';";
            if (await check.ExecuteScalarAsync(ct) is null) return;
        }

        var rows = new List<AuthUser>();
        await using (var read = con.CreateCommand())
        {
            read.CommandText =
                "SELECT id, username, password_hash, organisation_id, is_active, must_change_password, created_utc FROM users;";
            await using var r = await read.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new AuthUser
                {
                    Id = r.GetString(0),
                    Username = r.GetString(1),
                    PasswordHash = r.GetString(2),
                    OrganisationId = r.GetString(3),
                    Role = "Member",
                    Permissions = "state.read state.write",
                    IsActive = r.GetInt64(4) != 0,
                    MustChangePassword = r.GetInt64(5) != 0,
                    AccessStartsUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    AccessEndsUtc = DateTimeOffset.UtcNow.AddYears(10),
                });
            }
        }

        if (rows.Count == 0) return;

        var moved = 0;
        foreach (var u in rows)
            if (await target.CreateAsync(u, ct) == UserCreateResult.Created)
                moved++;

        logger.LogWarning("Auth carry-over: copied {Moved}/{Total} account(s) from 'users' to 'auth_users'.",
            moved, rows.Count);
    }
}
