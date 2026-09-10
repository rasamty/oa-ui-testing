# Repriori.BuildingBlocks.Auth

Reusable sign-in for ASP.NET Core apps. Drop it in and you get:

* **JWT access tokens** (short-lived, stateless) + **rotating refresh tokens** (single-use, cookie-borne)
* **TOTP two-factor** (RFC 6238) with QR enrolment and one-time recovery codes — opt-in per account
* **Claim-based authorization** — `perm:*` policies driven by the token, no database hit on the hot path
* A **full admin API** — create / disable / reset / revoke, with an immediate **token denylist**
* **Time-boxed access windows** (trials) enforced on login, on refresh, and in the token's own lifetime
* Per-username **lockout**, PBKDF2 password hashing, a username/password **policy**
* A small **CLI** for the first account on a fresh box
* **SQLite** storage out of the box (its own `auth_*` tables), swappable via interfaces

It knows nothing about any product — no product types, no product strings. A consuming app keeps its own data in its own tables keyed by the stable account id.

---

## Contents

1. [Install](#install)
2. [Quick start](#quick-start)
3. [The HTTP API](#the-http-api)
4. [Configuration — `AuthOptions`](#configuration--authoptions)
5. [How tokens work](#how-tokens-work)
6. [Two-factor](#two-factor)
7. [Authorization policies](#authorization-policies)
8. [The admin API](#the-admin-api)
9. [The CLI](#the-cli)
10. [Front-end](#front-end)
11. [Data model](#data-model)
12. [Services and extension points](#services-and-extension-points)
13. [Security notes](#security-notes)

---

## Install

```
dotnet add package Repriori.BuildingBlocks.Auth
```

Targets **net10.0**. Brings `Microsoft.Data.Sqlite` and `Microsoft.IdentityModel.JsonWebTokens`; everything else (endpoint routing, `HttpContext`, `PasswordHasher`, data protection) comes from the ASP.NET Core shared framework.

---

## Quick start

Three calls wire the whole thing.

```csharp
using BuildingBlocks.Auth.Authentication;
using BuildingBlocks.Auth.Authorization;
using BuildingBlocks.Auth.DependencyInjection;
using BuildingBlocks.Auth.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// 1. register everything (options bound from the "Auth" section)
builder.Services.AddBuildingBlocksAuth(builder.Configuration);

// 2. the bearer scheme — validates "Authorization: Bearer <jwt>" against the library
builder.Services
    .AddAuthentication(BearerAuthenticationHandler.SchemeName)
    .AddBuildingBlocksBearer();

// 3. the perm:* policies (the library's state.read / state.write / users.admin,
//    plus any product-specific names you pass in)
builder.Services.AddBuildingBlocksAuthorization("reports.export");

var app = builder.Build();

// create the auth_* tables once, before the first request
await app.Services.GetRequiredService<BuildingBlocks.Auth.Users.IUserStore>().EnsureSchemaAsync();

app.UseAuthentication();
app.UseAuthorization();

// 4. mount the endpoints (under any prefix you like)
app.MapBuildingBlocksAuth("/api/auth");

// your protected routes
app.MapGet("/api/things", () => "...")
   .RequireAuthorization(AuthPolicies.Policy(AuthPolicies.StateRead));
app.MapPut("/api/things", () => "...")
   .RequireAuthorization(AuthPolicies.Policy(AuthPolicies.StateWrite));

app.Run();
```

**The signing key is not configuration.** It is read from the `AUTH_SIGNING_KEY`
environment variable the first time the token service is resolved. It must be at
least 32 characters. Pass a `signingKeyOverride` argument to `AddBuildingBlocksAuth`
**only from tests**.

```csharp
// in appsettings.json — everything EXCEPT the signing key
{
  "Auth": {
    "Issuer": "https://app.example.com",
    "Audience": "example-api",
    "AccessTokenMinutes": 15,
    "RefreshTokenDays": 7,
    "RequireTwoFactor": false,
    "Database": { "Path": "Data/auth.db" }
  }
}
```

To keep the `auth_*` tables in the **same SQLite file** as your product data:

```csharp
builder.Services.PostConfigure<AuthDatabaseOptions>(o =>
    o.Path = builder.Configuration["MyApp:Sqlite:DbPath"]);
```

---

## The HTTP API

`app.MapBuildingBlocksAuth("/api/auth")` mounts these. All bodies and responses are
flat JSON (camelCase). Replace `/api/auth` with your prefix.

### Sign-in

| Method & path | Body | Returns |
|---|---|---|
| `POST /api/auth/login` | `{ username, password }` | `200` **TokenResponse**, or `200` `{ ticket }` if the account has 2FA, or `401` wrong credentials, `403` disabled / outside access window, `429` locked out |
| `POST /api/auth/login/2fa` | `{ ticket, code }` | `200` **TokenResponse** (`code` may be a TOTP **or** a recovery code), or `401` |
| `POST /api/auth/refresh` | *(refresh cookie)* — or `{ refreshToken }` for non-browser clients | `200` **TokenResponse** with a rotated refresh token, or `401` |
| `POST /api/auth/logout` | *(refresh cookie)* | `204` — revokes this session and clears the cookie |
| `GET  /api/auth/me` | *(bearer)* | `200` **MeResponse** |

### Self-service (bearer required)

| Method & path | Body | Notes |
|---|---|---|
| `POST /api/auth/change-password` | `{ currentPassword, newPassword }` | Clears `mustChangePassword`, **drops every other session**, returns a fresh **TokenResponse**. `400` on wrong current / policy. |
| `POST /api/auth/change-username` | `{ newUsername, currentPassword }` | The account id never changes. Returns a fresh **TokenResponse** with the new `name`. `400` taken / policy / unchanged. |
| `POST /api/auth/2fa/setup` | — | `200` **TwoFactorSetupResponse** `{ secret, otpAuthUri, alreadyEnabled }`. Stores a pending secret; 2FA is still off. |
| `POST /api/auth/2fa/confirm` | `{ code }` | `200` **RecoveryCodesResponse** `{ recoveryCodes: [...] }` — 2FA is now on. `400` wrong code. |
| `POST /api/auth/2fa/disable` | `{ currentPassword }` | `204`. Wipes the secret + recovery codes and drops every session. |

### Admin (`perm:users.admin` — the `Admin` role satisfies it)

| Method & path | Body | Notes |
|---|---|---|
| `GET  /api/auth/admin/users` | — (`?org=` filter) | list of **AdminUserView** |
| `GET  /api/auth/admin/users/{id}` | — | one **AdminUserView**, `404` if unknown |
| `POST /api/auth/admin/users` | `{ username, password, organisationId?, role?, permissions?, mustChangePassword?, trialDays? }` | `201` **AdminUserView** |
| `POST /api/auth/admin/users/{id}/disable` | — | denylists the user + drops refresh tokens (see [How tokens work](#how-tokens-work)) |
| `POST /api/auth/admin/users/{id}/enable` | — | clears the denylist entry |
| `POST /api/auth/admin/users/{id}/reset-password` | `{ newPassword, mustChange? }` | denylists + drops sessions |
| `POST /api/auth/admin/users/{id}/reset-2fa` | — | turns 2FA off, wipes recovery codes, denylists |
| `POST /api/auth/admin/users/{id}/access-window` | `{ startsUtc?, endsUtc? }` | set an explicit trial window |
| `POST /api/auth/admin/users/{id}/extend-trial` | `{ days }` | push the end out from `max(now, current end)` |
| `POST /api/auth/admin/users/{id}/revoke-sessions` | — | "sign this user out everywhere" |
| `DELETE /api/auth/admin/users/{id}` | — | `204`. Cascades to the user's tokens / codes. |

An admin **cannot** disable or delete their own account (`400`).

### Response shapes

```jsonc
// TokenResponse
{
  "accessToken": "<jwt>",
  "accessExpiresUtc": "2026-01-01T09:15:00Z",
  "tokenType": "Bearer",
  "refreshToken": "<id>.<secret>",       // also set as an HttpOnly cookie
  "refreshExpiresUtc": "2026-01-08T09:00:00Z",
  "mustChangePassword": false,
  "user": { /* MeResponse */ }
}

// MeResponse
{
  "id": "…", "username": "ada", "organisationId": "acme", "role": "Member",
  "permissions": ["state.read", "state.write"],
  "twoFactorEnabled": false,
  "accessEndsUtc": "2026-01-15T09:00:00Z"
}
```

The access-token claims are `sub` (the id), `name`, `org`, `role`, one `perm` per
permission, `amr` (`pwd` / `pwd otp` / `pwd rc` / `refresh`), `jti`, `iat`, `exp`.

---

## Configuration — `AuthOptions`

Bound from the `Auth` section. Every property, with its default:

| Property | Default | Meaning |
|---|---|---|
| `Issuer` | `"repriori"` | `iss` claim, checked on validation |
| `Audience` | `"repriori-apis"` | `aud` claim, checked on validation |
| `AccessTokenMinutes` | `15` | access-token lifetime |
| `RefreshTokenDays` | `7` | refresh-token lifetime (rotates on every use) |
| `RevokeAllOnRefreshReuse` | `false` | when a **revoked** refresh token is replayed, also revoke every other session for that user (aggressive theft response — a racy client can log itself out, so it is off by default) |
| `DefaultTrialDays` | `14` | trial length for a new account with no explicit dates |
| `DefaultPermissions` | `"state.read state.write"` | `perm` claims a new account gets when none are specified |
| `RequireTwoFactor` | `false` | require an OTP at login for any account that has a TOTP secret |
| `LockoutAttempts` | `8` | failed sign-ins before a username is locked |
| `LockoutMinutes` | `15` | how long a lockout lasts (also the window failures are counted in) |
| `TotpIssuerName` | `"Repriori"` | shown in the authenticator app on enrolment |
| `RefreshCookieName` | `"align_rt"` | name of the refresh-token cookie |
| `RefreshCookieSecure` | `null` | force the `Secure` flag; `null` = follow the request scheme. Set `true` behind a TLS-terminating proxy. |
| `CookieDomain` | `null` | pin the refresh cookie to a public hostname (proxy on a different host than the app sees) |
| `ClockSkewSeconds` | `30` | allowance when validating a token's lifetime |
| `Password.MinLength` | `12` | |
| `Password.MustDifferFromUsername` | `true` | |
| `Username.MinLength` / `MaxLength` | `3` / `32` | |
| `Username.Pattern` | `^[a-zA-Z0-9._-]+$` | |

`AuthDatabaseOptions.Path` (bound from `Auth:Database`) — the SQLite file for the
`auth_*` tables. Default `"Data/auth.db"`. Relative paths resolve against the app
base directory.

---

## How tokens work

```
  password ok
     │
     ▼
  ┌──────────────┐   access token (JWT, 15 min, stateless)
  │  POST /login │──────────────────────────────────────────────► client keeps
  │              │   refresh token ({id}.{secret}, 7 days)          in MEMORY
  └──────────────┘──────────────────────────────────────────────► HttpOnly cookie
                                                                   scoped to /api/auth

  every API call:  Authorization: Bearer <access token>
     │
     ▼
  BearerAuthenticationHandler
     ├─ signature + iss/aud/exp ...................... AccessTokenService
     └─ denylist: is this user's cut-off after iat? .. ITokenDenylist  ── 401 if so
     
  access token expired (or about to be):
     │
     ▼
  ┌────────────────┐  old refresh token is revoked, a NEW pair is issued
  │ POST /refresh  │  (rotation). Replaying a revoked token is always rejected;
  │  (cookie)      │  with RevokeAllOnRefreshReuse it also burns the family.
  └────────────────┘
```

**The token denylist is how "disable" / "reset password" / "revoke sessions" take
effect immediately.** Each of those admin actions writes a per-user *cut-off
instant* to `auth_token_denylist` and drops the user's refresh tokens. The bearer
handler checks the cut-off against each token's `iat` on every request, so an
already-issued access token stops working on the *next* request — not 15 minutes
later. `SqliteTokenDenylist` keeps a tiny in-process snapshot (reloaded every 15 s,
and immediately after a local write) so the check is not a database round-trip.

**Trial windows** are enforced three ways: `LoginService` refuses a login outside
the window, `/refresh` refuses to renew, and `AccessTokenService` caps each
token's `exp` at `AccessEndsUtc` so a trial that ends mid-session stops the token.

---

## Two-factor

Off by default. Any account can turn it on:

1. `POST /2fa/setup` → `{ secret, otpAuthUri }`. Render `otpAuthUri` as a QR (or show `secret` for manual entry). The secret is stored **data-protected**; 2FA is still off.
2. `POST /2fa/confirm { code }` → the user proves they added it. 2FA is now on and the response carries **10 one-time recovery codes** (shown once).
3. At the next login, `POST /login` returns `{ ticket }` instead of tokens. `POST /login/2fa { ticket, code }` completes it. `code` can be a TOTP **or** a recovery code (each recovery code works once).
4. `POST /2fa/disable { currentPassword }` turns it off and wipes the codes.

`TotpService` is a standalone RFC 6238 implementation (HMAC-SHA1, 30 s, 6 digits) —
no extra dependency. `ITotpSecretProtector` wraps the secret before storage; the
default uses ASP.NET data protection, with a pass-through `NullTotpSecretProtector`
for tests / hosts without it.

---

## Authorization policies

`AddBuildingBlocksAuthorization(params string[] extraPermissions)` registers one
policy per permission. `"state.write"` becomes the policy `"perm:state.write"`; get
the name with `AuthPolicies.Policy("state.write")`.

A policy passes when the caller is **authenticated AND** (`Admin` role **OR** carries
that exact `perm` claim). Nothing hits the database — the token is the
authorization data.

```csharp
app.MapPut("/api/things", ...)
   .RequireAuthorization(AuthPolicies.Policy(AuthPolicies.StateWrite));
```

Built-in constants: `AuthPolicies.StateRead`, `.StateWrite`, `.UsersAdmin`,
`.AdminRole`.

---

## The admin API

All of it is also on `AdminService` if you want to call it from code rather than
over HTTP:

```csharp
var admin = sp.GetRequiredService<BuildingBlocks.Auth.Admin.AdminService>();
await admin.SetActiveAsync(actingUserId, targetUserId, active: false);
await admin.ResetPasswordAsync(targetUserId, "A-New-Temp-Passphrase", mustChange: true);
await admin.ExtendTrialAsync(targetUserId, days: 30);
await admin.RevokeSessionsAsync(targetUserId);
```

Every mutation that restricts a user (`disable`, `reset-password`, `reset-2fa`,
`revoke-sessions`) cuts the denylist and drops that user's refresh tokens.

---

## The CLI

For the first account on a fresh box, before anyone can sign in. Delegate to it
from your host's `Program.cs`:

```csharp
if (args is ["auth", .. var rest])
{
    var host = Host.CreateApplicationBuilder(args);
    host.Services.AddBuildingBlocksAuth(host.Configuration);
    return await BuildingBlocks.Auth.Cli.AuthCli.RunAsync(rest, host.Build().Services);
}
```

```
auth create-user     --username U --password P --org O [--role R] [--perms "a b c"]
                      [--trial-days N] [--no-force-change]
auth list-users       [--org O]
auth show-user        --username U
auth reset-password   --username U --password P [--no-force-change]
auth disable          --username U
auth enable           --username U
auth set-role         --username U --role R          (e.g. Admin)
auth grant            --username U --perms "a b c"    (replaces the permission set)
```

---

## Front-end

The token model is: **access token in memory, refresh token in an HttpOnly cookie**.
A minimal browser client:

```js
let accessToken = null;

async function login(username, password) {
  const r = await fetch("/api/auth/login", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  const body = await r.json();
  if (body.ticket) return { twoFactorRequired: true, ticket: body.ticket };
  accessToken = body.accessToken;
  return { ok: r.ok };
}

async function refresh() {                       // uses the cookie
  const r = await fetch("/api/auth/refresh", { method: "POST" });
  if (!r.ok) { accessToken = null; return false; }
  accessToken = (await r.json()).accessToken;
  return true;
}

async function authFetch(url, opts = {}) {       // attaches the token, refreshes once on 401
  const withAuth = () => ({ ...opts, headers: { ...opts.headers, Authorization: `Bearer ${accessToken}` } });
  let r = await fetch(url, withAuth());
  if (r.status === 401 && await refresh()) r = await fetch(url, withAuth());
  return r;
}
```

De-duplicate concurrent `refresh()` calls onto one in-flight promise — a refresh
token is single-use, and two overlapping refreshes make the second look like a
replayed token.

---

## Data model

All tables are `CREATE ... IF NOT EXISTS` (`AuthSchema.Sql`) and live in one SQLite
file, separate from any product schema.

| Table | Holds |
|---|---|
| `auth_users` | the account: id, username (+ lower for CI uniqueness), password hash, org, role, permissions, active flag, must-change flag, 2FA flag + protected secret, access window, timestamps |
| `auth_refresh_tokens` | one row per issued refresh token — SHA-256 hash only, expiry, revoked-at, replaced-by |
| `auth_login_tickets` | single-use tickets for the password→OTP gap |
| `auth_recovery_codes` | SHA-256 of each recovery code, used-at |
| `auth_lockouts` | failed-attempt counter per username |
| `auth_token_denylist` | one row per revoked user — the cut-off instant |

---

## Services and extension points

Registered by `AddBuildingBlocksAuth`:

| Service | Role |
|---|---|
| `IUserStore` → `SqliteUserStore` | account CRUD |
| `UserProvisioningService` | the one correct way to create an account (policy + hash + trial window) |
| `PasswordService` | PBKDF2 hash/verify + policy |
| `PasswordChangeService` / `UsernameChangeService` | self-service |
| `TotpService`, `TwoFactorService`, `RecoveryCodeService`, `ITotpSecretProtector` | 2FA |
| `AccessTokenService` (+ `AuthSigningKey`) | mint / validate the JWT |
| `IRefreshTokenStore` → `SqliteRefreshTokenStore`, `RefreshTokenService` | refresh tokens |
| `ITokenDenylist` → `SqliteTokenDenylist` | immediate revocation |
| `ILoginAttemptTracker` → `SqliteLoginAttemptTracker` | lockout |
| `ILoginTicketStore` → `SqliteLoginTicketStore` | 2FA tickets |
| `LoginService` | the front door |
| `AdminService` | the admin operations |

Swap any storage by registering your own implementation of the `I…Store` /
`I…Tracker` / `ITokenDenylist` interface **before** calling `AddBuildingBlocksAuth`
(they are added with `TryAdd`-friendly ordering — register first to win) or after
it with an explicit `services.Replace(...)`.

---

## Security notes

* **Signing key**: `AUTH_SIGNING_KEY` env var only, ≥ 32 chars. Never appsettings, never source. Rotate by changing it (all existing access tokens become invalid; users refresh into new ones).
* **Refresh cookie**: `HttpOnly`, `SameSite=Lax`, path-scoped to the auth prefix. Set `RefreshCookieSecure=true` and `CookieDomain` when a proxy terminates TLS on another hostname.
* **Password hash**: PBKDF2 via ASP.NET's `PasswordHasher`. In load tests only, lower `PasswordHasherOptions.IterationCount`.
* **Timing**: an unknown username still runs a hash comparison (`PasswordService.DummyHash`), so "no such user" and "wrong password" take the same time.
* **Stored secrets**: password hashes, TOTP secrets (data-protected), refresh tokens (SHA-256), recovery codes (SHA-256) — no reversible secret is written.
* **Theft response**: refresh rotation always rejects a replayed revoked token; `RevokeAllOnRefreshReuse` escalates that to dropping every session.
