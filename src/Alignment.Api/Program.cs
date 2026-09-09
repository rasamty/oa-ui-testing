using System.Security.Claims;
using Alignment.Api.Data;
using Alignment.Api.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

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
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
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

await app.Services.GetRequiredService<IStateRepository>().EnsureSchemaAsync();

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

    await users.SetLastLoginAsync(user.Id, ct);

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

    // Idempotently ensure a sign-in account exists (the suite signs in once in setup).
    app.MapPost("/api/test/user", async (LoginRequest req, IUserRepository users,
        IPasswordHasher<User> hasher, CancellationToken ct) =>
    {
        var name = req.username ?? "playwright";
        if (await users.FindByUsernameAsync(name, ct) is not null)
            return Results.Ok(new { created = false });

        await users.CreateAsync(new User
        {
            Id = Guid.NewGuid().ToString("n"),
            Username = name,
            PasswordHash = hasher.HashPassword(null!, req.password ?? ""),
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
