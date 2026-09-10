using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Totp;

namespace BuildingBlocks.Auth.Tests;

public class TwoFactorServiceTests
{
    private const string Pw = "Correct-Horse-Battery-Staple";

    [Fact]
    public async Task Setup_then_confirm_turns_2fa_on_and_returns_recovery_codes()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);

        var setup = await h.TwoFactor.BeginSetupAsync(u.Id);
        Assert.NotNull(setup);
        Assert.False(setup!.AlreadyEnabled);
        Assert.StartsWith("otpauth://totp/", setup.OtpAuthUri);

        // account now has a pending secret but 2FA is still off
        Assert.False((await h.Users.FindByIdAsync(u.Id))!.TwoFactorEnabled);

        var secret = setup.Secret.Replace(" ", "");
        var wrong = await h.TwoFactor.ConfirmAsync(u.Id, "000000");
        Assert.Equal(TwoFactorConfirmOutcome.WrongCode, wrong.Outcome);

        var code = h.Totp.ComputeCurrent(secret, h.Clock.GetUtcNow());
        var confirmed = await h.TwoFactor.ConfirmAsync(u.Id, code);
        Assert.True(confirmed.Ok);
        Assert.Equal(10, confirmed.RecoveryCodes!.Count);
        Assert.True((await h.Users.FindByIdAsync(u.Id))!.TwoFactorEnabled);
    }

    [Fact]
    public async Task After_enrolment_login_needs_an_otp_and_a_recovery_code_also_works()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        var setup = await h.TwoFactor.BeginSetupAsync(u.Id);
        var secret = setup!.Secret.Replace(" ", "");
        var codes = (await h.TwoFactor.ConfirmAsync(u.Id, h.Totp.ComputeCurrent(secret, h.Clock.GetUtcNow()))).RecoveryCodes!;

        // password alone -> ticket
        var first = await h.Login.PasswordLoginAsync("ada", Pw);
        Assert.Equal(LoginOutcome.TwoFactorRequired, first.Outcome);

        // wrong otp
        Assert.Equal(LoginOutcome.InvalidCredentials,
            (await h.Login.CompleteTwoFactorAsync(first.TwoFactorTicket!, "123456")).Outcome);

        // a fresh ticket + a recovery code
        var second = await h.Login.PasswordLoginAsync("ada", Pw);
        var viaRecovery = await h.Login.CompleteTwoFactorAsync(second.TwoFactorTicket!, codes[0]);
        Assert.Equal(LoginOutcome.Success, viaRecovery.Outcome);
        Assert.Equal("pwd rc", (await h.Access.ValidateAsync(viaRecovery.AccessToken!))!.FindFirst("amr")!.Value);

        // that recovery code is now spent
        var third = await h.Login.PasswordLoginAsync("ada", Pw);
        Assert.Equal(LoginOutcome.InvalidCredentials,
            (await h.Login.CompleteTwoFactorAsync(third.TwoFactorTicket!, codes[0])).Outcome);
        Assert.Equal(9, await h.Recovery.RemainingAsync(u.Id));
    }

    [Fact]
    public async Task Disable_needs_the_password_and_then_wipes_everything()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        var setup = await h.TwoFactor.BeginSetupAsync(u.Id);
        var secret = setup!.Secret.Replace(" ", "");
        await h.TwoFactor.ConfirmAsync(u.Id, h.Totp.ComputeCurrent(secret, h.Clock.GetUtcNow()));

        Assert.Equal(TwoFactorDisableOutcome.WrongPassword, await h.TwoFactor.DisableAsync(u.Id, "nope"));
        Assert.Equal(TwoFactorDisableOutcome.Disabled, await h.TwoFactor.DisableAsync(u.Id, Pw));

        var after = await h.Users.FindByIdAsync(u.Id);
        Assert.False(after!.TwoFactorEnabled);
        Assert.Null(after.TotpSecretProtected);
        Assert.Equal(0, await h.Recovery.RemainingAsync(u.Id));

        // and login no longer asks for a code
        Assert.Equal(LoginOutcome.Success, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
    }

    [Fact]
    public async Task Recovery_codes_normalise_case_and_dashes()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        var codes = await h.Recovery.GenerateAsync(u.Id);

        var messy = codes[0].ToUpperInvariant().Replace("-", " ");
        Assert.True(await h.Recovery.ConsumeAsync(u.Id, messy));
        Assert.False(await h.Recovery.ConsumeAsync(u.Id, codes[0])); // already used
    }
}
