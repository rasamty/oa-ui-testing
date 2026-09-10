using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Users;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Auth.Cli;

/// <summary>
/// A tiny command-line surface for account admin, meant to be delegated to from a
/// host's <c>Program.cs</c> when it is started with an <c>auth</c> verb:
/// <code>
/// if (args is ["auth", .. var rest])
///     return await AuthCli.RunAsync(rest, host.Services);
/// </code>
/// Everything it does also exists in the HTTP admin API — this is for the first
/// account on a fresh box, before anyone can sign in.
/// </summary>
public static class AuthCli
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter? outWriter = null)
    {
        var @out = outWriter ?? Console.Out;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(@out);
            return args.Length == 0 ? 1 : 0;
        }

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IUserStore>();
        await store.EnsureSchemaAsync();

        var verb = args[0];
        var opts = ParseOptions(args.Skip(1));

        switch (verb)
        {
            case "create-user":
                return await CreateUser(sp, opts, @out);
            case "list-users":
                return await ListUsers(store, opts, @out);
            case "show-user":
                return await ShowUser(store, opts, @out);
            case "reset-password":
                return await ResetPassword(sp, store, opts, @out);
            case "disable":
                return await SetActive(store, opts, active: false, @out);
            case "enable":
                return await SetActive(store, opts, active: true, @out);
            default:
                @out.WriteLine($"unknown command '{verb}'");
                PrintUsage(@out);
                return 1;
        }
    }

    private static async Task<int> CreateUser(IServiceProvider sp, Dictionary<string, string> o, TextWriter @out)
    {
        if (!Require(o, @out, out var missing, "username", "password", "org")) return missing;

        var provisioning = sp.GetRequiredService<UserProvisioningService>();
        var result = await provisioning.CreateAsync(new NewUserRequest
        {
            Username = o["username"],
            Password = o["password"],
            OrganisationId = o["org"],
            Role = o.GetValueOrDefault("role", "Member"),
            Permissions = o.GetValueOrDefault("perms", ""),
            MustChangePassword = !o.ContainsKey("no-force-change"),
            AccessEndsUtc = o.TryGetValue("trial-days", out var d) && int.TryParse(d, out var days)
                ? DateTimeOffset.UtcNow.AddDays(days)
                : null,
        });

        if (!result.Ok)
        {
            @out.WriteLine($"error: {result.Error}");
            return 1;
        }
        @out.WriteLine($"created {result.User!.Username}  id={result.User.Id}  org={result.User.OrganisationId}");
        @out.WriteLine($"  role={result.User.Role}  perms=[{result.User.Permissions}]  mustChangePassword={result.User.MustChangePassword}");
        @out.WriteLine($"  access {result.User.AccessStartsUtc:u} .. {result.User.AccessEndsUtc:u}");
        return 0;
    }

    private static async Task<int> ListUsers(IUserStore store, Dictionary<string, string> o, TextWriter @out)
    {
        var users = await store.ListAsync(o.GetValueOrDefault("org"));
        if (users.Count == 0) { @out.WriteLine("(no users)"); return 0; }
        foreach (var u in users)
            @out.WriteLine($"{(u.IsActive ? " " : "x")} {u.Username,-24} {u.OrganisationId,-16} {u.Role,-10} " +
                           $"2fa={(u.TwoFactorEnabled ? "on " : "off")} ends={u.AccessEndsUtc:yyyy-MM-dd}");
        return 0;
    }

    private static async Task<int> ShowUser(IUserStore store, Dictionary<string, string> o, TextWriter @out)
    {
        if (!Require(o, @out, out var missing, "username")) return missing;
        var u = await store.FindByUsernameAsync(o["username"]);
        if (u is null) { @out.WriteLine("not found"); return 1; }
        @out.WriteLine($"id                   {u.Id}");
        @out.WriteLine($"username             {u.Username}");
        @out.WriteLine($"organisation         {u.OrganisationId}");
        @out.WriteLine($"role                 {u.Role}");
        @out.WriteLine($"permissions          {u.Permissions}");
        @out.WriteLine($"active               {u.IsActive}");
        @out.WriteLine($"mustChangePassword   {u.MustChangePassword}");
        @out.WriteLine($"twoFactorEnabled     {u.TwoFactorEnabled}");
        @out.WriteLine($"access window        {u.AccessStartsUtc:u} .. {u.AccessEndsUtc:u}");
        @out.WriteLine($"created              {u.CreatedUtc:u}");
        @out.WriteLine($"lastLogin            {(u.LastLoginUtc is { } l ? l.ToString("u") : "never")}");
        return 0;
    }

    private static async Task<int> ResetPassword(
        IServiceProvider sp, IUserStore store, Dictionary<string, string> o, TextWriter @out)
    {
        if (!Require(o, @out, out var missing, "username", "password")) return missing;
        var u = await store.FindByUsernameAsync(o["username"]);
        if (u is null) { @out.WriteLine("not found"); return 1; }

        var passwords = sp.GetRequiredService<PasswordService>();
        var pwError = passwords.ValidatePassword(o["password"], u.Username);
        if (pwError is not null) { @out.WriteLine($"error: {pwError}"); return 1; }

        var updated = u with
        {
            PasswordHash = passwords.Hash(o["password"]),
            MustChangePassword = !o.ContainsKey("no-force-change"),
        };
        await store.UpdateAsync(updated);
        @out.WriteLine($"password reset for {u.Username}  mustChangePassword={updated.MustChangePassword}");
        return 0;
    }

    private static async Task<int> SetActive(IUserStore store, Dictionary<string, string> o, bool active, TextWriter @out)
    {
        if (!Require(o, @out, out var missing, "username")) return missing;
        var u = await store.FindByUsernameAsync(o["username"]);
        if (u is null) { @out.WriteLine("not found"); return 1; }
        await store.UpdateAsync(u with { IsActive = active });
        @out.WriteLine($"{u.Username} is now {(active ? "enabled" : "disabled")}");
        return 0;
    }

    /// <summary>Parse <c>--key value</c> and bare <c>--flag</c> pairs into a dictionary (keys without the dashes).</summary>
    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pending = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) dict[pending] = "true";
                pending = a[2..];
            }
            else if (pending is not null)
            {
                dict[pending] = a;
                pending = null;
            }
        }
        if (pending is not null) dict[pending] = "true";
        return dict;
    }

    private static bool Require(Dictionary<string, string> o, TextWriter @out, out int exitCode, params string[] keys)
    {
        var missing = keys.Where(k => !o.ContainsKey(k)).ToArray();
        if (missing.Length == 0) { exitCode = 0; return true; }
        @out.WriteLine($"missing required option(s): {string.Join(", ", missing.Select(m => "--" + m))}");
        exitCode = 1;
        return false;
    }

    private static void PrintUsage(TextWriter @out)
    {
        @out.WriteLine("""
            auth <command> [options]

              create-user     --username U --password P --org O [--role R] [--perms "a b c"]
                              [--trial-days N] [--no-force-change]
              list-users      [--org O]
              show-user       --username U
              reset-password  --username U --password P [--no-force-change]
              disable         --username U
              enable          --username U
            """);
    }
}
