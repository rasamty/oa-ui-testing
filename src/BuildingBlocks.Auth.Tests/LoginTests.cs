using BuildingBlocks.Auth;
using BuildingBlocks.Auth.Admin;
using BuildingBlocks.Auth.Data;
using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Totp;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Tests;

public sealed class LoginHarness : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"authlogin-{Guid.NewGuid():n}.db");

    public AuthOptions Options { get; }
    public IUserStore Users { get; }
    public UserProvisioningService Provisioning { get; }
    public LoginService Login { get; }
    public RefreshTokenService Refresh { get; }
    public AccessTokenService Access { get; }
    public TotpService Totp { get; } = new();
    public RecoveryCodeService Recovery { get; }
    public TwoFactorService TwoFactor { get; }
    public ITokenDenylist Denylist { get; }
    public AdminService Admin { get; }
    public FakeClock Clock { get; } = new(DateTimeOffset.Parse("2026-01-01T09:00:00Z"));

    public LoginHarness(Action<AuthOptions>? configure = null)
    {
        Options = new AuthOptions { LockoutAttempts = 3, LockoutMinutes = 15, AccessTokenMinutes = 15, RefreshTokenDays = 7 };
        configure?.Invoke(Options);
        var opts = Microsoft.Extensions.Options.Options.Create(Options);

        var db = AuthDatabase.ForFile(_dbPath);
        var passwords = new PasswordService(new PasswordHasher<AuthUser>(), opts);
        Users = new SqliteUserStore(db);
        Provisioning = new UserProvisioningService(Users, passwords, opts, Clock);
        Access = new AccessTokenService(opts, AuthSigningKey.FromString("unit-test-signing-key-0123456789-abcdef"));
        Refresh = new RefreshTokenService(new SqliteRefreshTokenStore(db), opts, Clock);
        var lockouts = new SqliteLoginAttemptTracker(db, opts, Clock);
        var tickets = new SqliteLoginTicketStore(db, Clock);
        var protector = new NullTotpSecretProtector();
        var recoveryStore = new SqliteRecoveryCodeStore(db);
        Recovery = new RecoveryCodeService(recoveryStore, Clock);
        TwoFactor = new TwoFactorService(Users, Totp, protector, Recovery, recoveryStore, passwords, Refresh, opts, Clock);
        Login = new LoginService(Users, passwords, Access, Refresh, lockouts, tickets, Totp,
            protector, Recovery, opts, Clock);
        Denylist = new SqliteTokenDenylist(db, Clock);
        Admin = new AdminService(Users, Provisioning, passwords, Refresh, Denylist, recoveryStore, Clock);
    }

    public async Task<AuthUser> AddUserAsync(string username, string password,
        bool twoFactor = false, string? totpSecret = null, bool active = true,
        DateTimeOffset? endsUtc = null, bool mustChangePassword = false, string org = "acme")
    {
        var provisioned = await Provisioning.CreateAsync(new NewUserRequest
        {
            Username = username, Password = password, OrganisationId = org, MustChangePassword = mustChangePassword,
        });
        var u = provisioned.User! with
        {
            IsActive = active,
            TwoFactorEnabled = twoFactor,
            TotpSecretProtected = totpSecret,
            AccessEndsUtc = endsUtc ?? provisioned.User!.AccessEndsUtc,
        };
        await Users.UpdateAsync(u);
        return u;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }

    public sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

public class LoginServiceTests
{
    private const string Pw = "Correct-Horse-Battery-Staple";

    [Fact]
    public async Task Good_password_returns_a_validatable_access_token_and_a_refresh_token()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw);

        var result = await h.Login.PasswordLoginAsync("ada", Pw);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(result.RefreshToken);
        var principal = await h.Access.ValidateAsync(result.AccessToken!);
        Assert.NotNull(principal);
        Assert.Equal("ada", principal!.Identity!.Name);
    }

    [Fact]
    public async Task Wrong_password_is_invalid_and_unknown_user_looks_identical()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw);

        Assert.Equal(LoginOutcome.InvalidCredentials, (await h.Login.PasswordLoginAsync("ada", "nope-nope-nope")).Outcome);
        Assert.Equal(LoginOutcome.InvalidCredentials, (await h.Login.PasswordLoginAsync("ghost", "nope-nope-nope")).Outcome);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_then_a_good_password_is_still_refused()
    {
        using var h = new LoginHarness(o => o.LockoutAttempts = 3);
        await h.AddUserAsync("ada", Pw);

        for (var i = 0; i < 3; i++)
            await h.Login.PasswordLoginAsync("ada", "wrong-wrong-wrong");

        var locked = await h.Login.PasswordLoginAsync("ada", Pw);
        Assert.Equal(LoginOutcome.LockedOut, locked.Outcome);
        Assert.NotNull(locked.LockedOutUntil);
    }

    [Fact]
    public async Task Lockout_clears_after_the_window_passes()
    {
        using var h = new LoginHarness(o => { o.LockoutAttempts = 3; o.LockoutMinutes = 15; });
        await h.AddUserAsync("ada", Pw);
        for (var i = 0; i < 3; i++) await h.Login.PasswordLoginAsync("ada", "wrong-wrong-wrong");

        h.Clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(LoginOutcome.Success, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
    }

    [Fact]
    public async Task Disabled_account_cannot_sign_in()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw, active: false);
        Assert.Equal(LoginOutcome.Disabled, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
    }

    [Fact]
    public async Task Expired_access_window_cannot_sign_in()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw, endsUtc: h.Clock.GetUtcNow().AddDays(-1));
        Assert.Equal(LoginOutcome.OutsideAccessWindow, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
    }

    [Fact]
    public async Task MustChangePassword_still_signs_in_but_flags_it()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw, mustChangePassword: true);
        var result = await h.Login.PasswordLoginAsync("ada", Pw);
        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.True(result.MustChangePassword);
    }

    [Fact]
    public async Task Two_factor_account_needs_a_ticket_then_the_right_otp()
    {
        var secret = new TotpService().GenerateSecret();
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw, twoFactor: true, totpSecret: secret);

        var first = await h.Login.PasswordLoginAsync("ada", Pw);
        Assert.Equal(LoginOutcome.TwoFactorRequired, first.Outcome);
        Assert.NotNull(first.TwoFactorTicket);

        var wrong = await h.Login.CompleteTwoFactorAsync(first.TwoFactorTicket!, "000000");
        Assert.Equal(LoginOutcome.InvalidCredentials, wrong.Outcome);

        // ticket was single-use — need a fresh one
        var second = await h.Login.PasswordLoginAsync("ada", Pw);
        var otp = h.Totp.ComputeCurrent(secret, h.Clock.GetUtcNow());
        var ok = await h.Login.CompleteTwoFactorAsync(second.TwoFactorTicket!, otp);
        Assert.Equal(LoginOutcome.Success, ok.Outcome);
        Assert.Equal("pwd otp", (await h.Access.ValidateAsync(ok.AccessToken!))!.FindFirst("amr")!.Value);
    }
}

public class RefreshTokenServiceTests
{
    private const string Pw = "Correct-Horse-Battery-Staple";

    [Fact]
    public async Task Rotate_issues_a_new_token_and_burns_the_old_one()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw);
        var first = await h.Refresh.IssueAsync(user.Id);

        var rotated = await h.Refresh.RotateAsync(first.Raw);
        Assert.True(rotated.Ok);
        Assert.NotEqual(first.Raw, rotated.Next!.Raw);

        // the old token no longer works
        var replay = await h.Refresh.RotateAsync(first.Raw);
        Assert.Equal(RefreshOutcome.Reused, replay.Outcome);
    }

    [Fact]
    public async Task Replaying_a_revoked_token_is_rejected_but_leaves_other_sessions_alone_by_default()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw);
        var a = await h.Refresh.IssueAsync(user.Id);
        var b = await h.Refresh.RotateAsync(a.Raw);          // a -> b
        Assert.Equal(RefreshOutcome.Reused, (await h.Refresh.RotateAsync(a.Raw)).Outcome);

        Assert.True((await h.Refresh.RotateAsync(b.Next!.Raw)).Ok); // b still works
    }

    [Fact]
    public async Task Replaying_a_revoked_token_burns_the_family_when_the_option_is_on()
    {
        using var h = new LoginHarness(o => o.RevokeAllOnRefreshReuse = true);
        var user = await h.AddUserAsync("ada", Pw);
        var a = await h.Refresh.IssueAsync(user.Id);
        var b = await h.Refresh.RotateAsync(a.Raw);          // a -> b
        _ = await h.Refresh.RotateAsync(a.Raw);              // replay a  => burn everything

        var useB = await h.Refresh.RotateAsync(b.Next!.Raw); // b is dead too
        Assert.Equal(RefreshOutcome.Reused, useB.Outcome);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        using var h = new LoginHarness(o => o.RefreshTokenDays = 7);
        var user = await h.AddUserAsync("ada", Pw);
        var t = await h.Refresh.IssueAsync(user.Id);

        h.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(RefreshOutcome.Expired, (await h.Refresh.RotateAsync(t.Raw)).Outcome);
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        using var h = new LoginHarness();
        Assert.Equal(RefreshOutcome.Unknown, (await h.Refresh.RotateAsync("deadbeef.not-a-real-token")).Outcome);
    }

    [Fact]
    public async Task RevokeAll_kills_every_active_token_for_a_user()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw);
        var t1 = await h.Refresh.IssueAsync(user.Id);
        var t2 = await h.Refresh.IssueAsync(user.Id);

        var n = await h.Refresh.RevokeAllAsync(user.Id);
        Assert.Equal(2, n);
        Assert.False((await h.Refresh.RotateAsync(t1.Raw)).Ok);
        Assert.False((await h.Refresh.RotateAsync(t2.Raw)).Ok);
    }
}
