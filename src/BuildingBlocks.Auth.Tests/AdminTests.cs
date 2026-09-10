using BuildingBlocks.Auth.Admin;
using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Users;

namespace BuildingBlocks.Auth.Tests;

public class TokenDenylistTests
{
    [Fact]
    public async Task A_token_issued_before_the_cutoff_is_denied_a_later_one_is_not()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", "Correct-Horse-Battery-Staple");
        var issuedEarly = h.Clock.GetUtcNow();

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Denylist.DenyBeforeAsync(user.Id, h.Clock.GetUtcNow());

        Assert.True(await h.Denylist.IsDeniedAsync(user.Id, issuedEarly));
        Assert.False(await h.Denylist.IsDeniedAsync(user.Id, h.Clock.GetUtcNow().AddSeconds(5)));
    }

    [Fact]
    public async Task Clearing_the_cutoff_lets_old_tokens_back_in()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", "Correct-Horse-Battery-Staple");
        var issued = h.Clock.GetUtcNow();
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Denylist.DenyBeforeAsync(user.Id, h.Clock.GetUtcNow());
        Assert.True(await h.Denylist.IsDeniedAsync(user.Id, issued));

        await h.Denylist.ClearAsync(user.Id);
        Assert.False(await h.Denylist.IsDeniedAsync(user.Id, issued));
    }
}

public class AdminServiceTests
{
    private const string Pw = "Correct-Horse-Battery-Staple";

    [Fact]
    public async Task Disable_cuts_off_live_tokens_and_kills_refresh_immediately()  // acceptance check #9
    {
        using var h = new LoginHarness();
        var admin = await h.AddUserAsync("boss", Pw);
        var user = await h.AddUserAsync("ada", Pw);

        var (token, _) = h.Access.Issue(user, "pwd");
        var refresh = await h.Refresh.IssueAsync(user.Id);

        // token is fine right now
        Assert.NotNull(await h.Access.ValidateAsync(token));
        Assert.False(await h.Denylist.IsDeniedAsync(user.Id, h.Clock.GetUtcNow()));

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await h.Admin.SetActiveAsync(admin.Id, user.Id, active: false);
        Assert.True(result.Ok);

        // the still-valid token is now denied, and the refresh token is dead
        Assert.True(await h.Denylist.IsDeniedAsync(user.Id, h.Clock.GetUtcNow().AddSeconds(-30)));
        Assert.False((await h.Refresh.RotateAsync(refresh.Raw)).Ok);
        Assert.False((await h.Login.PasswordLoginAsync("ada", Pw)).Outcome == LoginOutcome.Success);
    }

    [Fact]
    public async Task Re_enabling_clears_the_cutoff()
    {
        using var h = new LoginHarness();
        var admin = await h.AddUserAsync("boss", Pw);
        var user = await h.AddUserAsync("ada", Pw);
        await h.Admin.SetActiveAsync(admin.Id, user.Id, false);
        await h.Admin.SetActiveAsync(admin.Id, user.Id, true);

        var old = h.Clock.GetUtcNow().AddMinutes(-10);
        Assert.False(await h.Denylist.IsDeniedAsync(user.Id, old));
        Assert.Equal(LoginOutcome.Success, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
    }

    [Fact]
    public async Task An_admin_cannot_disable_or_delete_their_own_account()
    {
        using var h = new LoginHarness();
        var admin = await h.AddUserAsync("boss", Pw);
        Assert.Equal(AdminOutcome.NotAllowed, (await h.Admin.SetActiveAsync(admin.Id, admin.Id, false)).Outcome);
        Assert.Equal(AdminOutcome.NotAllowed, await h.Admin.DeleteAsync(admin.Id, admin.Id));
    }

    [Fact]
    public async Task Reset_password_forces_a_change_and_revokes_sessions()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw);
        var refresh = await h.Refresh.IssueAsync(user.Id);

        var r = await h.Admin.ResetPasswordAsync(user.Id, "A-New-Temp-Passphrase-1", mustChange: true);
        Assert.True(r.Ok);
        Assert.True(r.User!.MustChangePassword);
        Assert.False((await h.Refresh.RotateAsync(refresh.Raw)).Ok);

        Assert.Equal(LoginOutcome.InvalidCredentials, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
        var ok = await h.Login.PasswordLoginAsync("ada", "A-New-Temp-Passphrase-1");
        Assert.Equal(LoginOutcome.Success, ok.Outcome);
        Assert.True(ok.MustChangePassword);
    }

    [Fact]
    public async Task Reset_2fa_turns_it_off_and_clears_recovery_codes()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw);
        var setup = await h.TwoFactor.BeginSetupAsync(user.Id);
        await h.TwoFactor.ConfirmAsync(user.Id, h.Totp.ComputeCurrent(setup!.Secret.Replace(" ", ""), h.Clock.GetUtcNow()));
        Assert.True((await h.Users.FindByIdAsync(user.Id))!.TwoFactorEnabled);

        var r = await h.Admin.ResetTwoFactorAsync(user.Id);
        Assert.True(r.Ok);
        Assert.False((await h.Users.FindByIdAsync(user.Id))!.TwoFactorEnabled);
        Assert.Equal(0, await h.Recovery.RemainingAsync(user.Id));
    }

    [Fact]
    public async Task Extend_trial_pushes_the_end_out_and_reopens_a_lapsed_account()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw, endsUtc: h.Clock.GetUtcNow().AddDays(-1)); // already lapsed
        Assert.Equal(LoginOutcome.OutsideAccessWindow, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);

        var r = await h.Admin.ExtendTrialAsync(user.Id, days: 30);
        Assert.True(r.Ok);
        Assert.True(r.User!.AccessEndsUtc > h.Clock.GetUtcNow().AddDays(29));
        Assert.Equal(LoginOutcome.Success, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
    }

    [Fact]
    public async Task Set_access_window_rejects_an_end_before_the_start()
    {
        using var h = new LoginHarness();
        var user = await h.AddUserAsync("ada", Pw);
        var r = await h.Admin.SetAccessWindowAsync(user.Id,
            h.Clock.GetUtcNow().AddDays(5), h.Clock.GetUtcNow().AddDays(1));
        Assert.Equal(AdminOutcome.PolicyViolation, r.Outcome);
    }

    [Fact]
    public async Task List_and_create_go_through_the_service()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("ada", Pw, org: "acme");
        var (outcome, _, created) = await h.Admin.CreateAsync(new NewUserRequest
        {
            Username = "grace", Password = Pw, OrganisationId = "acme",
        });
        Assert.Equal(AdminOutcome.Ok, outcome);
        Assert.NotNull(created);

        var acme = await h.Admin.ListAsync("acme");
        Assert.Contains(acme, u => u.Username == "grace");
    }
}
