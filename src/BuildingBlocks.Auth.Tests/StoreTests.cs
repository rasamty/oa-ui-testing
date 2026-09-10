using BuildingBlocks.Auth;
using BuildingBlocks.Auth.Data;
using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Tests;

/// <summary>Each test gets its own throwaway SQLite file under the temp directory.</summary>
public class SqliteUserStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"authtest-{Guid.NewGuid():n}.db");
    private readonly SqliteUserStoreFixture _fx;

    public SqliteUserStoreTests() => _fx = new SqliteUserStoreFixture(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }

    private sealed class SqliteUserStoreFixture
    {
        public SqliteUserStore Store { get; }
        public UserProvisioningService Provisioning { get; }

        public SqliteUserStoreFixture(string dbPath)
        {
            var options = Options.Create(new AuthOptions { DefaultTrialDays = 14 });
            var passwords = new PasswordService(new PasswordHasher<AuthUser>(), options);
            Store = new SqliteUserStore(AuthDatabase.ForFile(dbPath));
            Provisioning = new UserProvisioningService(Store, passwords, options);
        }
    }

    private static AuthUser NewUser(string username, string org = "acme") => new()
    {
        Id = Guid.NewGuid().ToString(),
        Username = username,
        PasswordHash = "hash",
        OrganisationId = org,
    };

    [Fact]
    public async Task Create_then_find_by_username_is_case_insensitive()
    {
        var u = NewUser("Ada.Lovelace");
        Assert.Equal(UserCreateResult.Created, await _fx.Store.CreateAsync(u));

        var found = await _fx.Store.FindByUsernameAsync("ada.lovelace");
        Assert.NotNull(found);
        Assert.Equal(u.Id, found!.Id);
        Assert.Equal("Ada.Lovelace", found.Username);
    }

    [Fact]
    public async Task Duplicate_username_is_rejected_regardless_of_case()
    {
        await _fx.Store.CreateAsync(NewUser("grace"));
        Assert.Equal(UserCreateResult.UsernameTaken, await _fx.Store.CreateAsync(NewUser("GRACE")));
    }

    [Fact]
    public async Task Update_round_trips_every_mutable_field()
    {
        var u = NewUser("edith");
        await _fx.Store.CreateAsync(u);

        var changed = u with
        {
            Username = "edith-clarke",
            PasswordHash = "hash2",
            Role = "Admin",
            Permissions = "state.read state.write users.admin",
            IsActive = false,
            MustChangePassword = true,
            TwoFactorEnabled = true,
            TotpSecretProtected = "protected-secret",
            AccessEndsUtc = DateTimeOffset.UtcNow.AddDays(30),
        };
        Assert.True(await _fx.Store.UpdateAsync(changed));

        var reloaded = await _fx.Store.FindByIdAsync(u.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("edith-clarke", reloaded!.Username);
        Assert.Equal("hash2", reloaded.PasswordHash);
        Assert.Equal("Admin", reloaded.Role);
        Assert.Contains("users.admin", reloaded.PermissionList());
        Assert.False(reloaded.IsActive);
        Assert.True(reloaded.TwoFactorEnabled);
        Assert.Equal("protected-secret", reloaded.TotpSecretProtected);
        Assert.Equal(changed.AccessEndsUtc.ToUnixTimeSeconds(), reloaded.AccessEndsUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Update_of_a_missing_id_returns_false()
        => Assert.False(await _fx.Store.UpdateAsync(NewUser("ghost")));

    [Fact]
    public async Task Rename_frees_the_old_username()
    {
        var u = NewUser("oldname");
        await _fx.Store.CreateAsync(u);
        await _fx.Store.UpdateAsync(u with { Username = "newname" });

        Assert.Null(await _fx.Store.FindByUsernameAsync("oldname"));
        Assert.Equal(UserCreateResult.Created, await _fx.Store.CreateAsync(NewUser("oldname")));
    }

    [Fact]
    public async Task List_filters_by_organisation_and_orders_by_name()
    {
        await _fx.Store.CreateAsync(NewUser("zoe", "beta"));
        await _fx.Store.CreateAsync(NewUser("amy", "beta"));
        await _fx.Store.CreateAsync(NewUser("someone", "gamma"));

        var beta = await _fx.Store.ListAsync("beta");
        Assert.Equal(new[] { "amy", "zoe" }, beta.Select(x => x.Username));
        Assert.DoesNotContain(beta, x => x.OrganisationId != "beta");
    }

    [Fact]
    public async Task Delete_removes_the_account()
    {
        var u = NewUser("temp");
        await _fx.Store.CreateAsync(u);
        Assert.True(await _fx.Store.DeleteAsync(u.Id));
        Assert.Null(await _fx.Store.FindByIdAsync(u.Id));
        Assert.False(await _fx.Store.DeleteAsync(u.Id));
    }

    [Fact]
    public async Task SetLastLogin_stamps_the_time()
    {
        var u = NewUser("stamp");
        await _fx.Store.CreateAsync(u);
        Assert.Null((await _fx.Store.FindByIdAsync(u.Id))!.LastLoginUtc);

        var when = DateTimeOffset.UtcNow;
        await _fx.Store.SetLastLoginAsync(u.Id, when);
        var after = (await _fx.Store.FindByIdAsync(u.Id))!.LastLoginUtc;
        Assert.NotNull(after);
        Assert.Equal(when.ToUnixTimeSeconds(), after!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Provisioning_applies_policy_hashing_and_trial_window()
    {
        var result = await _fx.Provisioning.CreateAsync(new NewUserRequest
        {
            Username = "provisioned",
            Password = "Correct-Horse-Battery-Staple",
            OrganisationId = "acme",
        });

        Assert.True(result.Ok);
        Assert.NotEqual("Correct-Horse-Battery-Staple", result.User!.PasswordHash);
        Assert.True(result.User.MustChangePassword);
        var days = (result.User.AccessEndsUtc - result.User.AccessStartsUtc).TotalDays;
        Assert.InRange(days, 13.9, 14.1);
    }

    [Theory]
    [InlineData("ab", "Correct-Horse-Battery-Staple", "3-32")]
    [InlineData("okname", "short", "at least 12")]
    public async Task Provisioning_rejects_bad_input(string username, string password, string expected)
    {
        var result = await _fx.Provisioning.CreateAsync(new NewUserRequest
        {
            Username = username, Password = password, OrganisationId = "acme",
        });
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error);
    }
}
