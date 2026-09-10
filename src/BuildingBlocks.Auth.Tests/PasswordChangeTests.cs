using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;

namespace BuildingBlocks.Auth.Tests;

public class PasswordChangeServiceTests
{
    private const string Pw = "Correct-Horse-Battery-Staple";

    private static PasswordChangeService Make(LoginHarness h) =>
        new(h.Users, new PasswordService(new Microsoft.AspNetCore.Identity.PasswordHasher<Users.AuthUser>(),
                Microsoft.Extensions.Options.Options.Create(h.Options)),
            h.Access, h.Refresh);

    [Fact]
    public async Task Wrong_current_password_is_rejected()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        var r = await Make(h).ChangeAsync(u.Id, "not-the-password", "A-Brand-New-Passphrase-9");
        Assert.Equal(PasswordChangeOutcome.WrongCurrentPassword, r.Outcome);
    }

    [Fact]
    public async Task A_weak_new_password_is_rejected()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        var r = await Make(h).ChangeAsync(u.Id, Pw, "short");
        Assert.Equal(PasswordChangeOutcome.PolicyViolation, r.Outcome);
        Assert.Contains("at least 12", r.Error);
    }

    [Fact]
    public async Task A_good_change_clears_the_flag_drops_other_sessions_and_returns_fresh_tokens()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw, mustChangePassword: true);
        var otherSession = await h.Refresh.IssueAsync(u.Id);

        var r = await Make(h).ChangeAsync(u.Id, Pw, "A-Brand-New-Passphrase-9");

        Assert.True(r.Ok);
        Assert.False(r.User!.MustChangePassword);
        Assert.NotNull(await h.Access.ValidateAsync(r.AccessToken!));
        Assert.NotNull(r.RefreshToken);

        // the pre-existing session is gone
        Assert.False((await h.Refresh.RotateAsync(otherSession.Raw)).Ok);
        // the new one works
        Assert.True((await h.Refresh.RotateAsync(r.RefreshToken!.Raw)).Ok);
        // and the new password verifies on a fresh login
        Assert.Equal(BuildingBlocks.Auth.Login.LoginOutcome.Success,
            (await h.Login.PasswordLoginAsync("ada", "A-Brand-New-Passphrase-9")).Outcome);
    }
}
