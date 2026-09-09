using Alignment.Api.Data;
using Alignment.Api.Model;

// ---- CLI mode: `dotnet run -- import <file>` ------------------------------------
if (args.Length > 0 && args[0] is "import")
{
    var host = Host.CreateApplicationBuilder(args);
    host.Services.AddSingleton<IStateRepository, SqliteStateRepository>();
    var app0 = host.Build();
    var repo0 = app0.Services.GetRequiredService<IStateRepository>();
    await repo0.EnsureSchemaAsync();

    if (args.Length < 2) { Console.Error.WriteLine("usage: dotnet run -- import <path-to-xlsx>"); return 1; }
    return await ExcelImporter.ImportAsync(repo0, args[1], org: "demo");
}

// ---- Web app -------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IStateRepository, SqliteStateRepository>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

await app.Services.GetRequiredService<IStateRepository>().EnsureSchemaAsync();

app.UseDefaultFiles();   // "/" -> wwwroot/index.html
app.UseStaticFiles();    // serve wwwroot/*

// Phase 1: one organisation, "demo". Phase 3 takes it from the auth token.
// In TestMode only, an ?org= query lets parallel Playwright workers stay isolated.
var testMode = app.Configuration.GetValue<bool>("Alignment:TestMode");
static string OrgOf(HttpContext ctx, bool testMode) =>
    testMode && ctx.Request.Query.TryGetValue("org", out var q) && !string.IsNullOrWhiteSpace(q)
        ? q.ToString()
        : "demo";

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    var state = await db.LoadAsync(OrgOf(ctx, testMode), ct);
    return state is null ? Results.Json(new { exists = false }) : Results.Json(state);
});

app.MapPut("/api/state", async (AlignmentState state, HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    await db.SaveAsync(OrgOf(ctx, testMode), state, ct);
    return Results.Ok(new { saved = true });
});

// The page's "Reset to defaults" button calls this so the reload that follows
// starts from the built-in defaults, not the stored state. Phase 3 scopes it to
// the caller's own organisation via the auth token.
app.MapDelete("/api/state", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
{
    await db.ResetAsync(OrgOf(ctx, testMode), ct);
    return Results.Ok(new { reset = true });
});

// Test-only: wipe one org so each Playwright test starts clean.
if (testMode)
{
    app.MapPost("/api/test/reset", async (HttpContext ctx, IStateRepository db, CancellationToken ct) =>
    {
        await db.ResetAsync(OrgOf(ctx, testMode), ct);
        return Results.Ok(new { reset = true });
    });
    app.Logger.LogWarning("Alignment:TestMode is ON — /api/test/reset is exposed and ?org= is honoured.");
}

app.Run();
return 0;

// Exposed so Alignment.Api.Tests can spin the app up with WebApplicationFactory.
public partial class Program;
