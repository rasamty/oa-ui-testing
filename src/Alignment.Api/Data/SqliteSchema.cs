using Microsoft.Data.Sqlite;

namespace Alignment.Api.Data;

/// <summary>
/// The alignment database schema. Embedded here (not a loose .sql file) so it
/// works identically under `dotnet run`, `dotnet test`, CI and Azure regardless
/// of the working directory. The canonical copy is also in Docs/ Appendix A.
/// </summary>
public static class SqliteSchema
{
    public const string Sql = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS organisations (
          id          TEXT PRIMARY KEY,
          name        TEXT NOT NULL,
          created_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS portfolios (
          id              TEXT PRIMARY KEY,
          organisation_id TEXT NOT NULL,
          name            TEXT NOT NULL,
          code            TEXT,
          sort_order      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS objectives (
          id              TEXT PRIMARY KEY,
          organisation_id TEXT NOT NULL,
          portfolio_id    TEXT NOT NULL REFERENCES portfolios(id) ON DELETE CASCADE,
          code            TEXT,
          label           TEXT NOT NULL,
          active          INTEGER NOT NULL DEFAULT 1,
          weight          REAL    NOT NULL DEFAULT 0,
          sort_order      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS metrics (
          id              TEXT PRIMARY KEY,
          organisation_id TEXT NOT NULL,
          portfolio_id    TEXT NOT NULL REFERENCES portfolios(id) ON DELETE CASCADE,
          code            TEXT,
          label           TEXT NOT NULL,
          active          INTEGER NOT NULL DEFAULT 1,
          sort_order      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS metric_objective_links (
          organisation_id TEXT NOT NULL,
          metric_id       TEXT NOT NULL REFERENCES metrics(id)    ON DELETE CASCADE,
          objective_id    TEXT NOT NULL REFERENCES objectives(id) ON DELETE CASCADE,
          strength        REAL NOT NULL CHECK (strength > 0),
          PRIMARY KEY (organisation_id, metric_id, objective_id)
        );

        CREATE TABLE IF NOT EXISTS objective_objective_links (
          organisation_id     TEXT NOT NULL,
          source_objective_id TEXT NOT NULL REFERENCES objectives(id) ON DELETE CASCADE,
          target_objective_id TEXT NOT NULL REFERENCES objectives(id) ON DELETE CASCADE,
          strength            REAL NOT NULL CHECK (strength > 0),
          PRIMARY KEY (organisation_id, source_objective_id, target_objective_id),
          CHECK (source_objective_id <> target_objective_id)
        );

        CREATE TABLE IF NOT EXISTS ui_state (
          organisation_id    TEXT PRIMARY KEY,
          mode               TEXT NOT NULL DEFAULT 'performance',
          left_portfolio_id  TEXT,
          right_portfolio_id TEXT
        );

        CREATE TABLE IF NOT EXISTS app_meta (
          organisation_id TEXT NOT NULL,
          key             TEXT NOT NULL,
          value           TEXT NOT NULL,
          PRIMARY KEY (organisation_id, key)
        );

        -- Phase 2: a person who can sign in. One organisation per user for now;
        -- Phase 3 turns organisation_id into a real multi-tenant boundary.
        CREATE TABLE IF NOT EXISTS users (
          id                   TEXT PRIMARY KEY,           -- generated GUID, never the username
          username             TEXT NOT NULL,              -- as typed / displayed
          username_lower       TEXT NOT NULL UNIQUE,       -- lower-cased: case-insensitive login + uniqueness
          password_hash        TEXT NOT NULL,              -- PasswordHasher output — never the raw password
          organisation_id      TEXT NOT NULL,              -- which board this user sees
          is_active            INTEGER NOT NULL DEFAULT 1, -- 0 = locked out on the next request
          must_change_password INTEGER NOT NULL DEFAULT 0, -- reserved for a future reset flow
          created_utc          TEXT NOT NULL,
          last_login_utc       TEXT                        -- null until the first successful login
        );
        """;
}
