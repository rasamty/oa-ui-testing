namespace BuildingBlocks.Auth.Data;

/// <summary>
/// The auth tables. Kept entirely separate from any product's tables — a product
/// never joins to these and this library never touches a product table.
/// All CREATE ... IF NOT EXISTS, so it is safe to run on every connection open.
/// </summary>
public static class AuthSchema
{
    public const string Sql = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS auth_users (
          id                     TEXT PRIMARY KEY,
          username               TEXT NOT NULL,
          username_lower         TEXT NOT NULL UNIQUE,
          password_hash          TEXT NOT NULL,
          organisation_id        TEXT NOT NULL,
          role                   TEXT NOT NULL DEFAULT 'Member',
          permissions            TEXT NOT NULL DEFAULT '',
          is_active              INTEGER NOT NULL DEFAULT 1,
          must_change_password   INTEGER NOT NULL DEFAULT 0,
          two_factor_enabled     INTEGER NOT NULL DEFAULT 0,
          totp_secret_protected  TEXT,
          access_starts_utc      TEXT NOT NULL,
          access_ends_utc        TEXT NOT NULL,
          created_utc            TEXT NOT NULL,
          last_login_utc         TEXT
        );

        CREATE TABLE IF NOT EXISTS auth_refresh_tokens (
          id            TEXT PRIMARY KEY,
          user_id       TEXT NOT NULL REFERENCES auth_users(id) ON DELETE CASCADE,
          token_hash    TEXT NOT NULL UNIQUE,   -- SHA-256 of the raw token; the raw value is never stored
          created_utc   TEXT NOT NULL,
          expires_utc   TEXT NOT NULL,
          revoked_utc   TEXT,
          replaced_by   TEXT                    -- the id of the token that rotated this one out
        );
        CREATE INDEX IF NOT EXISTS ix_refresh_user ON auth_refresh_tokens(user_id);

        CREATE TABLE IF NOT EXISTS auth_login_tickets (
          id           TEXT PRIMARY KEY,        -- opaque ticket id given to the client
          ticket_hash  TEXT NOT NULL,           -- SHA-256 of the secret part
          user_id      TEXT NOT NULL REFERENCES auth_users(id) ON DELETE CASCADE,
          purpose      TEXT NOT NULL,           -- 'otp' | 'change-password'
          created_utc  TEXT NOT NULL,
          expires_utc  TEXT NOT NULL,
          consumed_utc TEXT
        );

        CREATE TABLE IF NOT EXISTS auth_recovery_codes (
          user_id    TEXT NOT NULL REFERENCES auth_users(id) ON DELETE CASCADE,
          code_hash  TEXT NOT NULL,
          used_utc   TEXT,
          PRIMARY KEY (user_id, code_hash)
        );

        CREATE TABLE IF NOT EXISTS auth_lockouts (
          key            TEXT PRIMARY KEY,      -- username_lower (or ip, if the caller keys by ip)
          failed_count   INTEGER NOT NULL DEFAULT 0,
          first_fail_utc TEXT,
          lockout_until  TEXT
        );

        CREATE TABLE IF NOT EXISTS auth_token_denylist (
          user_id     TEXT PRIMARY KEY REFERENCES auth_users(id) ON DELETE CASCADE,
          since_utc   TEXT NOT NULL           -- reject access tokens issued before this instant
        );
        """;
}
