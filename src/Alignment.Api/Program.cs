using System.Security.Claims;
using Alignment.Api.Data;
using Alignment.Api.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

// Password hashing is synchronous and CPU-bound. A burst of logins (the parallel
// Playwright suite; or several testers at once) can otherwise stall Kestrel while
// the thread pool grows one-thread-per-second. Start with enough threads.
ThreadPool.SetMinThreads(workerThreads: 32, completionPortThreads: 32);

// ---- CLI mode: `dotnet run -- <command> …` ------------------------------------
//   import <file.xlsx>                         load a sample workbook into "demo"
//   create-user <username> <password> [org]    add a sign-in account
if (args.Length > 0 && args[0] is "import" or "create-user")
{
    var host = Host.CreateApplicationBuilder(args);
    host.Services.AddSingleton<IStateRepository, SqliteStateRepository>();
    host.Services.AddSingleton<IUserRepository, SqliteUserRepository>();
    host.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
    var cli = host.Build();

    if (args[0] is "import")
    {
        var repo0 = cli.Services.GetRequiredService<IStateRepository>();
        await repo0.EnsureSchemaAsync();
        if (args.Length < 2) { Console.Error.WriteLine("usage: dotnet run -- import <path-to-xlsx>"); return 1; }
        return await ExcelImporter.ImportAsync(repo0, args[1], org: "demo");
    }

    // create-user <username> <password> [organisation]
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: dotnet run -- create-user <username> <password> [organisation]");
        return 1;
    }

    var users = cli.Services.GetRequiredService<IUserRepository>();
    await users.EnsureSchemaAsync();

    var hasher = cli.Services.GetRequiredService<IPasswordHasher<User>>();
    var username = args[1];
    var org = args.Length >= 4 ? args[3] : "demo";

    var newUser = new User
    {
        Id = Guid.NewGuid().ToString("n"),
        Username = username,
        // HashPassword ignores the first argument in the current implementation.
        PasswordHash = hasher.HashPassword(null!, args[2]),
        OrganisationId = org,
    };

    if (!await users.CreateAsync(newUser))
    {
        Console.Error.WriteLine($"a user named '{username}' already exists");
        return 2;
    }

    Console.WriteLine($"created user '{username}' (id {newUser.Id}) in org '{org}'");
    return 0;
}

// ---- Web app -------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IStateRepository, SqliteStateRepository>();
builder.Services.AddSingleton<IUserRepository, SqliteUserRepository>();

// Phase 2 auth: hashes and verifies passwords. PasswordHasher never needs the
// User object itself in current ASP.NET, but the generic type is required.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// PBKDF2 is deliberately slow (~tens of ms per call). Under the parallel
// Playwright suite dozens of logins land at once and starve the dev server, so
// in TestMode only we cut the work right down — the passwords are throwaway.
if (builder.Configuration.GetValue<bool>("Alignment:TestMode"))
    builder.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1_000);

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Phase 2: sign-in is a cookie. Scheme name "Align". The cookie holds the user's
// id, name and org as claims; the server checks its signature on every request,
// no database hit. Phase 3 replaces this with bearer tokens.
builder.Services
    .AddAuthentication("Align")
    .AddCookie("Align", o =>
    {
        o.Cookie.Name = "align_auth";
        o.Cookie.HttpOnly = true;                       // JavaScript cannot read it
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest           // http on localhost is fine
            : CookieSecurePolicy.Always;                  // HTTPS only in production
        // When the app is reached through a proxy on a different hostname than the
        // one it thinks it is serving (e.g. Cloudflare fronting align.repriori.com
        // but forwarding to *.azurewebsites.net), pin the cookie to the public
        // hostname so the browser sends it back.
        var cookieDomain = builder.Configuration["Alignment:Auth:CookieDomain"];
        if (!string.IsNullOrWhiteSpace(cookieDomain))
            o.Cookie.Domain = cookieDomain;
        o.ExpireTimeSpan = TimeSpan.FromHours(
            builder.Configuration.GetValue("Alignment:Auth:SessionHours", 12.0)); // appsettings; restart to apply
        o.SlidingExpiration = true;                       // active use keeps it alive
        // This is an API, not a website: answer with status codes, never a redirect
        // to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Pre-computed once: a login attempt for a username that does not exist still
// runs the same password-hash work, so response time does not reveal which
// usernames are real.
var noSuchUserHash = app.Services.GetRequiredService<IPasswordHasher<User>>()
    .HashPassword(null!, "no-such-user");

// Create the tables once, at startup, before any request can race on it.
await app.Services.GetRequiredService<IStateRepository>().EnsureSchemaAsync();
await app.Services.GetRequiredService<IUserRepository>().EnsureSchemaAsync();

// First-run bootstrap: when Alignment:Bootstrap:User/Password are set (an
// app setting on the host) and that account does not exist yet, create it.
// Lets a fresh deployment get its first sign-in account without SSH. Idempotent
// — it never resets an existing user, so the settings can be left in place or
// removed after the first start.
{
    var bootUser = app.Configuration["Alignment:Bootstrap:User"];
    var bootPass = app.Configuration["Alignment:Bootstrap:Password"];
    if (!string.IsNullOrWhiteSpace(bootUser) && !string.IsNullOrWhiteSpace(bootPass))
    {
        var repo = app.Services.GetRequiredService<IUserRepository>();
        if (await repo.FindByUsernameAsync(bootUser) is null)
        {
            var hasher = app.Services.GetRequiredService<IPasswordHasher<User>>();
            await repo.CreateAsync(new User
            {
                Id = Guid.NewGuid().ToString("n"),
                Username = bootUser,
                PasswordHash = hasher.HashPassword(null!, bootPass),
                OrganisationId = "demo",
            });
            app.Logger.LogWarning("Bootstrap: created sign-in account '{User}'", bootUser);
        }
    }
}

app.UseDefaultFiles();   // "/" -> wwwroot/index.html
app.UseStaticFiles();    // serve wwwroot/* (the page and its assets stay public)

app.UseAuthentication();  // read the cookie, build ctx.User
app.UseAuthorization();   // enforce .RequireAuthorization() below

// The organisation whose board this request touches:
//   - TestMode + ?org=  -> that value (parallel Playwright workers stay isolated)
//   - signed in         -> the user's "org" claim
//   - otherwise         -> "demo"
var testMode = app.Configuration.GetValue<bool>("Alignment:TestMode");
static string OrgOf(HttpContext ctx, bool testMode)
{
    if (testMode && ctx.Request.Query.TryGetValue("org", out var q) && !string.IsNullOrWhiteSpace(q))
        return q.ToString();
    return ctx.User.FindFirstValue("org") ?? "demo";
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- auth ----
app.MapPost("/api/auth/login", async (LoginRequest req, HttpContext ctx,
    IUserRepository users, IPasswordHasher<User> hasher, CancellationToken ct) =>
{
    var user = await users.FindByUsernameAsync(req.username ?? "", ct);

    // Always verify a hash (the real one, or a dummy) so timing is the same
    // whether or not the username exists.
    var passwordOk = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? noSuchUserHash, req.password ?? "")
                     != PasswordVerificationResult.Failed;

    if (user is null || !user.IsActive || !passwordOk)
        return Results.Json(new { error = "wrong username or password" }, statusCode: StatusCodes.Status401Unauthorized);

    // Bookkeeping only — don't make the caller wait on a write, and don't let it
    // fail the login. (Keeps login off the single user-DB write lock.)
    _ = Task.Run(() => users.SetLastLoginAsync(user.Id, CancellationToken.None));

    var identity = new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim("org", user.OrganisationId),
    ], authenticationType: "Align");
    await ctx.SignInAsync("Align", new ClaimsPrincipal(identity));

    return Results.Ok(new { username = user.Username, organisationId = user.OrganisationId });
});

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("Align");
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/auth/me", (HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true
        ? Results.Ok(new
        {
            authenticated = true,
            username = ctx.User.Identity!.Name,
            organisationId = ctx.User.FindFirstValue("org"),
        })
        : Results.Json(new { authenticated = false }, statusCode: StatusCodes.Status401Unauthorized));

// Change your own password. Must be signed in and prove the current password.
app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, HttpContext ctx,
    IUserRepository users, IPasswordHasher<User> hasher, CancellationToken ct) =>
{
    var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var user = id is null ? null : await users.FindByIdAsync(id, ct);
    if (user is null)
        return Results.Json(new { error = "not signed in" }, statusCode: StatusCodes.Status401Unauthorized);

    var current = req.currentPassword ?? "";
    var next = req.newPassword ?? "";

    if (hasher.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed)
        return Results.Json(new { error = "current password is wrong" }, statusCode: StatusCodes.Status400BadRequest);
    if (next.Length < 8)
        return Results.Json(new { error = "new password must be at least 8 characters" }, statusCode: StatusCodes.Status400BadRequest);
    if (next == current)
        return Results.Json(new { error = "new password must be different" }, statusCode: StatusCodes.Status400BadRequest);

    await users.SetPasswordAsync(user.Id, hasher.HashPassword(user, next), ct);
    return Results.Ok(new { changed = true });
}).RequireAuthorization();

app.MapGet("/api/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    var state = await db.LoadAsync(OrgOf(ctx, testMode), ct);
    return state is null ? Results.Json(new { exists = false }) : Results.Json(state);
}).RequireAuthorization();

app.MapPut("/api/state", async (AlignmentState state, HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    await db.SaveAsync(OrgOf(ctx, testMode), state, ct);
    return Results.Ok(new { saved = true });
}).RequireAuthorization();

// The page's "Reset to defaults" button calls this so the reload that follows
// starts from the built-in defaults, not the stored state. Phase 3 scopes it to
// the caller's own organisation via the auth token.
app.MapDelete("/api/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    await db.ResetAsync(OrgOf(ctx, testMode), ct);
    return Results.Ok(new { reset = true });
}).RequireAuthorization();

// Test-only helpers so the Playwright suite can set itself up. Never in production.
if (testMode)
{
    app.MapPost("/api/test/reset", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
    {
        await db.ResetAsync(OrgOf(ctx, testMode), ct);
        return Results.Ok(new { reset = true });
    });

    // Force a sign-in account to exist with exactly this password and active.
    // Idempotent to a known state so a test that changes the password does not
    // leak into the next run (the test DB is not wiped between runs).
    app.MapPost("/api/test/user", async (LoginRequest req, IUserRepository users,
        IPasswordHasher<User> hasher, CancellationToken ct) =>
    {
        var name = req.username ?? "playwright";
        var hash = hasher.HashPassword(null!, req.password ?? "");

        var existing = await users.FindByUsernameAsync(name, ct);
        if (existing is not null)
        {
            await users.SetPasswordAsync(existing.Id, hash, ct);
            return Results.Ok(new { created = false });
        }

        await users.CreateAsync(new User
        {
            Id = Guid.NewGuid().ToString("n"),
            Username = name,
            PasswordHash = hash,
            OrganisationId = "demo",
        }, ct);
        return Results.Ok(new { created = true });
    });

    app.Logger.LogWarning("Alignment:TestMode is ON — /api/test/* is exposed and ?org= is honoured.");
}

app.Run();
return 0;

// Exposed so Alignment.Api.Tests can spin the app up with WebApplicationFactory.
public partial class Program;
