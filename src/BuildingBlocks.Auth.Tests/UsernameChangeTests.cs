using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Users;

namespace BuildingBlocks.Auth.Tests;

public class UsernameChangeServiceTests
{
    private const string Pw = "Correct-Horse-Battery-Staple";

    private static UsernameChangeService Make(LoginHarness h) =>
        new(h.Users,
            new PasswordService(new Microsoft.AspNetCore.Identity.PasswordHasher<AuthUser>(),
                Microsoft.Extensions.Options.Options.Create(h.Options)),
            h.Access, h.Refresh);

    [Fact]
    public async Task Changing_the_name_keeps_the_id_and_re_issues_a_token_with_the_new_name()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);

        var r = await Make(h).ChangeAsync(u.Id, "ada-lovelace", Pw);

        Assert.True(r.Ok);
        Assert.Equal(u.Id, r.User!.Id);                       // id is stable
        var principal = await h.Access.ValidateAsync(r.AccessToken!);
        Assert.Equal("ada-lovelace", principal!.Identity!.Name);
        Assert.Equal(u.Id, principal.FindFirst("sub")!.Value);

        // old name no longer resolves, new one does
        Assert.Null(await h.Users.FindByUsernameAsync("ada"));
        Assert.Equal(u.Id, (await h.Users.FindByUsernameAsync("ada-lovelace"))!.Id);
    }

    [Fact]
    public async Task Wrong_password_is_refused()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        Assert.Equal(UsernameChangeOutcome.WrongPassword, (await Make(h).ChangeAsync(u.Id, "ada2", "nope")).Outcome);
    }

    [Fact]
    public async Task A_taken_name_is_refused()
    {
        using var h = new LoginHarness();
        await h.AddUserAsync("bob", Pw);
        var u = await h.AddUserAsync("ada", Pw);
        var r = await Make(h).ChangeAsync(u.Id, "BOB", Pw);
        Assert.Equal(UsernameChangeOutcome.Taken, r.Outcome);
    }

    [Fact]
    public async Task An_invalid_name_is_refused()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        var r = await Make(h).ChangeAsync(u.Id, "no spaces", Pw);
        Assert.Equal(UsernameChangeOutcome.PolicyViolation, r.Outcome);
        Assert.Contains("letters", r.Error);
    }

    [Fact]
    public async Task Login_works_with_the_new_name_afterwards()
    {
        using var h = new LoginHarness();
        var u = await h.AddUserAsync("ada", Pw);
        await Make(h).ChangeAsync(u.Id, "ada-new", Pw);

        Assert.Equal(LoginOutcome.InvalidCredentials, (await h.Login.PasswordLoginAsync("ada", Pw)).Outcome);
        Assert.Equal(LoginOutcome.Success, (await h.Login.PasswordLoginAsync("ada-new", Pw)).Outcome);
    }
}
