using System.Text;
using BuildingBlocks.Auth;
using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Totp;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Tests;

public class TotpTests
{
    private readonly TotpService _totp = new();

    [Fact]
    public void A_generated_secret_verifies_its_own_current_code()
    {
        var secret = _totp.GenerateSecret();
        var code = _totp.ComputeCurrent(secret);
        Assert.True(_totp.Verify(secret, code));
    }

    [Fact]
    public void A_code_from_the_previous_window_still_verifies_with_skew_1()
    {
        var secret = _totp.GenerateSecret();
        var thirtySecondsAgo = DateTimeOffset.UtcNow.AddSeconds(-30);
        var oldCode = _totp.ComputeCurrent(secret, thirtySecondsAgo);
        Assert.True(_totp.Verify(secret, oldCode, stepSkew: 1));
    }

    [Fact]
    public void A_code_from_five_minutes_ago_does_not_verify()
    {
        var secret = _totp.GenerateSecret();
        var old = _totp.ComputeCurrent(secret, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(_totp.Verify(secret, old, stepSkew: 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Malformed_input_is_rejected_not_thrown(string code)
    {
        Assert.False(_totp.Verify(_totp.GenerateSecret(), code));
    }

    [Fact]
    public void Matches_the_RFC_6238_reference_vector()
    {
        // RFC 6238 Appendix B: secret "12345678901234567890" (ASCII), T=59s -> 8-digit "94287082".
        // We use 6 digits (what authenticator apps default to) => the last six: "287082".
        var secret = Base32Of("12345678901234567890");
        var code = _totp.ComputeCurrent(secret, DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal("287082", code);
    }

    [Fact]
    public void The_otpauth_uri_is_well_formed()
    {
        var uri = _totp.BuildOtpAuthUri("Repriori", "ada", "JBSWY3DPEHPK3PXP");
        Assert.StartsWith("otpauth://totp/Repriori:ada?", uri);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri);
        Assert.Contains("issuer=Repriori", uri);
    }

    private static string Base32Of(string ascii)
    {
        // small local Base32 encoder for the test vector
        const string alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var data = Encoding.ASCII.GetBytes(ascii);
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(alpha[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alpha[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}

public class PasswordTests
{
    private readonly PasswordService _svc = new(
        new PasswordHasher<AuthUser>(),
        Options.Create(new AuthOptions()));

    [Fact]
    public void Hash_is_not_the_password_and_verifies()
    {
        var hash = _svc.Hash("Correct-Horse-Battery-Staple");
        Assert.DoesNotContain("Correct-Horse-Battery-Staple", hash);
        Assert.True(_svc.Verify(hash, "Correct-Horse-Battery-Staple"));
        Assert.False(_svc.Verify(hash, "wrong"));
    }

    [Theory]
    [InlineData("short", "at least 12")]
    [InlineData("thisIsTwelveX", null)] // 13 chars, ok
    public void Password_policy_min_length(string pw, string? expectContains)
    {
        var result = _svc.ValidatePassword(pw, "someuser");
        if (expectContains is null) Assert.Null(result);
        else Assert.Contains(expectContains, result);
    }

    [Fact]
    public void Password_may_not_equal_the_username()
    {
        Assert.Contains("username", _svc.ValidatePassword("SameAsUserNm1", "SameAsUserNm1")!);
    }

    [Theory]
    [InlineData("ab", "3-32")]
    [InlineData("has spaces", "letters")]
    [InlineData("ok.name-1", null)]
    public void Username_policy(string name, string? expectContains)
    {
        var result = _svc.ValidateUsername(name);
        if (expectContains is null) Assert.Null(result);
        else Assert.Contains(expectContains, result);
    }
}

public class AccessTokenTests
{
    private static AccessTokenService Make(string key = "test-signing-key-0123456789-abcdefghijklmnop", int minutes = 15) =>
        new(Options.Create(new AuthOptions { AccessTokenMinutes = minutes, Issuer = "test-iss", Audience = "test-aud" }),
            AuthSigningKey.FromString(key));

    private static AuthUser Sample() => new()
    {
        Id = "u-123",
        Username = "ada",
        PasswordHash = "x",
        OrganisationId = "demo",
        Role = "Admin",
        Permissions = "state.read state.write users.admin",
    };

    [Fact]
    public async Task Issue_then_validate_round_trips_with_all_claims()
    {
        var svc = Make();
        var (token, exp) = svc.Issue(Sample(), amr: "pwd");
        Assert.True(exp > DateTimeOffset.UtcNow);

        var principal = await svc.ValidateAsync(token);
        Assert.NotNull(principal);
        Assert.Equal("u-123", principal!.FindFirst("sub")!.Value);
        Assert.Equal("ada", principal.Identity!.Name);
        Assert.Equal("demo", principal.FindFirst("org")!.Value);
        Assert.True(principal.IsInRole("Admin"));
        Assert.Contains("state.write", principal.FindAll("perm").Select(c => c.Value));
        Assert.Equal("pwd", principal.FindFirst("amr")!.Value);
    }

    [Fact]
    public async Task An_expired_token_does_not_validate()
    {
        var svc = Make(minutes: -1); // already expired
        var (token, _) = svc.Issue(Sample(), "pwd");
        Assert.Null(await svc.ValidateAsync(token));
    }

    [Fact]
    public async Task A_token_signed_with_a_different_key_does_not_validate()
    {
        var (token, _) = Make(key: "key-one-key-one-key-one-key-one-key-one").Issue(Sample(), "pwd");
        var other = Make(key: "key-two-key-two-key-two-key-two-key-two");
        Assert.Null(await other.ValidateAsync(token));
    }

    [Fact]
    public async Task A_tampered_token_does_not_validate()
    {
        var svc = Make();
        var (token, _) = svc.Issue(Sample(), "pwd");
        var tampered = token[..^4] + (token[^1] == 'a' ? "bbbb" : "aaaa");
        Assert.Null(await svc.ValidateAsync(tampered));
    }
}
