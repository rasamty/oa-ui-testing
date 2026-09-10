using System.Security.Claims;
using Alignment.Api.Data;
using Alignment.Api.Model;
using BuildingBlocks.Auth;
using BuildingBlocks.Auth.Authentication;
using BuildingBlocks.Auth.Authorization;
using BuildingBlocks.Auth.Cli;
using BuildingBlocks.Auth.Data;
using BuildingBlocks.Auth.DependencyInjection;
using BuildingBlocks.Auth.Endpoints;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Identity;

// Password hashing is synchronous and CPU-bound. A burst of logins (the parallel
// Playwright suite; or several testers at once) can otherwise stall Kestrel while
// the thread pool grows one-thread-per-second. Start with enough threads.
ThreadPool.SetMinThreads(workerThreads: 32, completionPortThreads: 32);

// ---- CLI mode: `dotnet run -- <command> …` ------------------------------------
//   import <file.xlsx>          load a sample workbook into "demo"
//   auth <subcommand> …         account admin (create-user, list-users, reset-password, …)
if (args.Length > 0 && args[0] is "import" or "auth")
{
    var cliHost = Host.CreateApplicationBuilder(args);
    cliHost.Services.AddSingleton<IStateRepository, SqliteStateRepository>();
    cliHost.Services.AddBuildingBlocksAuth(cliHost.Configuration);
    PointAuthDatabaseAtStateDatabase(cliHost.Services, cliHost.Configuration);
    var cli = cliHost.Build();

    if (args[0] is "import")
    {
        var repo0 = cli.Services.GetRequiredService<IStateRepository>();
        await repo0.EnsureSchemaAsync();
        if (args.Length < 2) { Console.Error.WriteLine("usage: dotnet run -- import <path-to-xlsx>"); return 1; }
        return await ExcelImporter.ImportAsync(repo0, args[1], org: "demo");
    }

    // auth <subcommand> …
    return await AuthCli.RunAsync(args[1..], cli.Services);
}

// ---- Web app -------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
var testMode = builder.Configuration.GetValue<bool>("Alignment:TestMode");

builder.Services.AddSingleton<IStateRepository, SqliteStateRepository>();

// The reusable sign-in library. Phase 3 replaced the Phase 2 hand-rolled cookie
// auth with this: JWT access tokens + rotating refresh tokens, all its own tables
// (auth_users, auth_refresh_tokens, …) in the same SQLite file as the board data.
//
// The JWT signing key comes from AUTH_SIGNING_KEY (environment), never appsettings.
// TestMode is allowed a fixed throwaway key so `dotnet run` / CI need no secret.
var signingKeyOverride = testMode && Environment.GetEnvironmentVariable("AUTH_SIGNING_KEY") is null
    ? "testmode-only-not-a-secret-signing-key-0123456789"
    : null;
builder.Services.AddBuildingBlocksAuth(builder.Configuration, signingKeyOverride);
PointAuthDatabaseAtStateDatabase(builder.Services, builder.Configuration);

// PBKDF2 is deliberately slow (~tens of ms). Under the parallel Playwright suite
// dozens of logins land at once and starve the dev server, so in TestMode only we
// cut the work right down — the passwords are throwaway.
if (testMode)
    builder.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1_000);

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// One scheme: the library's bearer handler validates `Authorization: Bearer <jwt>`
// against its own token service. No cookie scheme, no JwtBearer package.
builder.Services
    .AddAuthentication(BearerAuthenticationHandler.SchemeName)
    .AddBuildingBlocksBearer();
// Named "perm:*" policies from the token's perm claims. Admin role bypasses them.
builder.Services.AddBuildingBlocksAuthorization();

var app = builder.Build();

// Create every table once, at startup, before a request can race on it.
await app.Services.GetRequiredService<IStateRepository>().EnsureSchemaAsync();
await app.Services.GetRequiredService<IUserStore>().EnsureSchemaAsync();

// One-time carry-over: if the Phase 2 `users` table has accounts and the Phase 3
// `auth_users` table is still empty, copy them across (same id, hash, org). Lets a
// deployment that already had sign-ins keep them without anyone re-registering.
await AuthUserImport.CarryOverAsync(
    builder.Configuration["Alignment:Sqlite:DbPath"] ?? "Data/alignment.db",
    app.Services.GetRequiredService<IUserStore>(),
    app.Logger);

// First-run bootstrap: when Alignment:Bootstrap:User/Password are set (an app
// setting on the host) and that account does not exist, create it as an admin.
// Idempotent — never resets an existing user.
{
    var bootUser = app.Configuration["Alignment:Bootstrap:User"];
    var bootPass = app.Configuration["Alignment:Bootstrap:Password"];
    if (!string.IsNullOrWhiteSpace(bootUser) && !string.IsNullOrWhiteSpace(bootPass))
    {
        var store = app.Services.GetRequiredService<IUserStore>();
        if (await store.FindByUsernameAsync(bootUser) is null)
        {
            var provisioning = app.Services.GetRequiredService<UserProvisioningService>();
            var result = await provisioning.CreateAsync(new NewUserRequest
            {
                Username = bootUser,
                Password = bootPass,
                OrganisationId = "demo",
                Role = "Admin",
                Permissions = "state.read state.write users.admin",
                MustChangePassword = false,
                AccessEndsUtc = DateTimeOffset.UtcNow.AddYears(10),
            });
            if (result.Ok)
                app.Logger.LogWarning("Bootstrap: created admin sign-in account '{User}'", bootUser);
            else
                app.Logger.LogError("Bootstrap: could not create '{User}': {Error}", bootUser, result.Error);
        }
    }
}

app.UseDefaultFiles();   // "/" -> wwwroot/index.html
app.UseStaticFiles();    // serve wwwroot/* (the page and its assets stay public)

app.UseAuthentication();  // validate the bearer token, build ctx.User
app.UseAuthorization();   // enforce .RequireAuthorization() below

// The organisation whose board this request touches:
//   - TestMode + ?org=  -> that value (parallel Playwright workers stay isolated)
//   - signed in         -> the user's "org" claim
//   - otherwise         -> "demo"
static string OrgOf(HttpContext ctx, bool testMode)
{
    if (testMode && ctx.Request.Query.TryGetValue("org", out var q) && !string.IsNullOrWhiteSpace(q))
        return q.ToString();
    return ctx.User.FindFirstValue("org") ?? "demo";
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// All the sign-in endpoints, from the library, under /api/auth:
//   POST /api/auth/login              username + password  -> tokens (or a 2FA ticket)
//   POST /api/auth/login/2fa          ticket + OTP         -> tokens
//   POST /api/auth/refresh            (refresh cookie)     -> fresh tokens
//   POST /api/auth/logout             revoke this session
//   GET  /api/auth/me                 the signed-in account
//   POST /api/auth/change-password    change own password  -> fresh tokens
app.MapBuildingBlocksAuth("/api/auth");

// Reading the board needs "state.read"; changing or clearing it needs "state.write".
// A read-only member has only the first, so their PUT/DELETE come back 403.
app.MapGet("/api/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    var state = await db.LoadAsync(OrgOf(ctx, testMode), ct);
    return state is null ? Results.Json(new { exists = false }) : Results.Json(state);
}).RequireAuthorization(AuthPolicies.Policy(AuthPolicies.StateRead));

app.MapPut("/api/state", async (AlignmentState state, HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    await db.SaveAsync(OrgOf(ctx, testMode), state, ct);
    return Results.Ok(new { saved = true });
}).RequireAuthorization(AuthPolicies.Policy(AuthPolicies.StateWrite));

// The page's "Reset to defaults" button calls this so the reload that follows
// starts from the built-in defaults, not the stored state.
app.MapDelete("/api/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    await db.ResetAsync(OrgOf(ctx, testMode), ct);
    return Results.Ok(new { reset = true });
}).RequireAuthorization(AuthPolicies.Policy(AuthPolicies.StateWrite));

// Test-only helpers so the Playwright suite can set itself up. Never in production.
if (testMode)
{
    app.MapPost("/api/test/reset", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
    {
        await db.ResetAsync(OrgOf(ctx, testMode), ct);
        return Results.Ok(new { reset = true });
    });

    // Read a board's state without a token — so a test can assert what the DB holds
    // without threading the in-page access token through page.request.
    app.MapGet("/api/test/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
    {
        var state = await db.LoadAsync(OrgOf(ctx, testMode), ct);
        return state is null ? Results.Json(new { exists = false }) : Results.Json(state);
    });

    // Force a sign-in account to exist with exactly this password, active, and with
    // no forced password change — a known state so a test that changes the password
    // does not leak into the next run (the test DB is not wiped between runs).
    app.MapPost("/api/test/user", async (TestUserRequest req, IUserStore users, UserProvisioningService provisioning,
        BuildingBlocks.Auth.Passwords.PasswordService passwords, CancellationToken ct) =>
    {
        var name = string.IsNullOrWhiteSpace(req.username) ? "playwright" : req.username!;
        var pass = req.password ?? "";
        var org = string.IsNullOrWhiteSpace(req.org) ? "demo" : req.org!;

        var role = string.IsNullOrWhiteSpace(req.role) ? "Member" : req.role!;
        var perms = string.IsNullOrWhiteSpace(req.permissions) ? "state.read state.write" : req.permissions!;

        var existing = await users.FindByUsernameAsync(name, ct);
        if (existing is not null)
        {
            // Reset to a fully known state — including 2FA off and the requested
            // role/permissions — so a re-used deterministic username starts clean.
            await users.UpdateAsync(existing with
            {
                PasswordHash = passwords.Hash(pass),
                OrganisationId = org,
                Role = role,
                Permissions = perms,
                IsActive = true,
                MustChangePassword = false,
                TwoFactorEnabled = false,
                TotpSecretProtected = null,
                AccessEndsUtc = DateTimeOffset.UtcNow.AddYears(10),
            }, ct);
            return Results.Ok(new { created = false });
        }

        var result = await provisioning.CreateAsync(new NewUserRequest
        {
            Username = name,
            Password = pass,
            OrganisationId = org,
            Role = role,
            Permissions = perms,
            MustChangePassword = false,
            AccessEndsUtc = DateTimeOffset.UtcNow.AddYears(10),
        }, ct);
        return result.Ok
            ? Results.Ok(new { created = true })
            : Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);
    });

    app.Logger.LogWarning("Alignment:TestMode is ON — /api/test/* is exposed and ?org= is honoured.");
}

app.Run();
return 0;

// Keep the library's auth database in the same SQLite file as the board data —
// one file to back up, one path to configure (Alignment:Sqlite:DbPath).
static void PointAuthDatabaseAtStateDatabase(IServiceCollection services, IConfiguration config)
{
    var path = config["Alignment:Sqlite:DbPath"];
    if (!string.IsNullOrWhiteSpace(path))
        services.PostConfigure<AuthDatabaseOptions>(o => o.Path = path);
}

/// <summary>Body of <c>POST /api/test/user</c> (TestMode only).</summary>
public sealed record TestUserRequest(string? username, string? password, string? org, string? role, string? permissions);

// Exposed so Alignment.Api.Tests can spin the app up with WebApplicationFactory.
public partial class Program;
