using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Totp;
using BuildingBlocks.Auth.Users;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Login;

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    LockedOut,
    Disabled,
    OutsideAccessWindow,
    TwoFactorRequired,
}

/// <summary>
/// The result of a login attempt. On <see cref="LoginOutcome.Success"/> the tokens
/// are populated; on <see cref="LoginOutcome.TwoFactorRequired"/> the
/// <see cref="TwoFactorTicket"/> is — call <see cref="LoginService.CompleteTwoFactorAsync"/>
/// with it and the OTP.
/// </summary>
public sealed record LoginResult
{
    public required LoginOutcome Outcome { get; init; }
    public AuthUser? User { get; init; }
    public string? AccessToken { get; init; }
    public DateTimeOffset? AccessExpiresUtc { get; init; }
    public IssuedRefreshToken? RefreshToken { get; init; }
    public string? TwoFactorTicket { get; init; }
    public DateTimeOffset? LockedOutUntil { get; init; }

    /// <summary>True when the sign-in worked but the user must change their password before doing anything else.</summary>
    public bool MustChangePassword { get; init; }

    internal static LoginResult Fail(LoginOutcome o, DateTimeOffset? until = null) =>
        new() { Outcome = o, LockedOutUntil = until };
}

/// <summary>
/// The front door. Verifies a username + password (plus lockout, active flag and
/// trial window), then either issues tokens or — if the account has 2FA — a ticket
/// to exchange for tokens once the OTP checks out.
/// </summary>
public sealed class LoginService
{
    private static readonly TimeSpan TwoFactorTicketLifetime = TimeSpan.FromMinutes(5);

    private readonly IUserStore _users;
    private readonly PasswordService _passwords;
    private readonly AccessTokenService _access;
    private readonly RefreshTokenService _refresh;
    private readonly ILoginAttemptTracker _lockouts;
    private readonly ILoginTicketStore _tickets;
    private readonly TotpService _totp;
    private readonly ITotpSecretProtector _protector;
    private readonly RecoveryCodeService _recovery;
    private readonly AuthOptions _opts;
    private readonly TimeProvider _clock;

    public LoginService(
        IUserStore users,
        PasswordService passwords,
        AccessTokenService access,
        RefreshTokenService refresh,
        ILoginAttemptTracker lockouts,
        ILoginTicketStore tickets,
        TotpService totp,
        ITotpSecretProtector protector,
        RecoveryCodeService recovery,
        IOptions<AuthOptions> opts,
        TimeProvider? clock = null)
    {
        _users = users;
        _passwords = passwords;
        _access = access;
        _refresh = refresh;
        _lockouts = lockouts;
        _tickets = tickets;
        _totp = totp;
        _protector = protector;
        _recovery = recovery;
        _opts = opts.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<LoginResult> PasswordLoginAsync(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        var lockout = await _lockouts.GetAsync(username, ct);
        if (lockout.IsLockedOut)
            return LoginResult.Fail(LoginOutcome.LockedOut, lockout.LockedOutUntil);

        var user = await _users.FindByUsernameAsync(username, ct);

        // Verify a hash even when the user does not exist, so a missing account and a
        // wrong password take the same amount of time.
        var hashToCheck = user?.PasswordHash ?? PasswordService.DummyHash;
        var passwordOk = _passwords.Verify(hashToCheck, password ?? "");

        if (user is null || !passwordOk)
        {
            var after = await _lockouts.RecordFailureAsync(username, ct);
            return LoginResult.Fail(LoginOutcome.InvalidCredentials, after.LockedOutUntil);
        }

        if (!user.IsActive)
            return LoginResult.Fail(LoginOutcome.Disabled);

        var now = _clock.GetUtcNow();
        if (now < user.AccessStartsUtc || now >= user.AccessEndsUtc)
            return LoginResult.Fail(LoginOutcome.OutsideAccessWindow);

        await _lockouts.ResetAsync(username, ct);

        var needsOtp = user.TwoFactorEnabled || (_opts.RequireTwoFactor && user.TotpSecretProtected is not null);
        if (needsOtp)
        {
            var ticket = await _tickets.IssueAsync(user.Id, "otp", TwoFactorTicketLifetime, ct);
            return new LoginResult { Outcome = LoginOutcome.TwoFactorRequired, User = user, TwoFactorTicket = ticket };
        }

        return await IssueForAsync(user, amr: "pwd", ct);
    }

    /// <summary>Second half of a 2FA login: exchange the ticket + current OTP for tokens.</summary>
    public async Task<LoginResult> CompleteTwoFactorAsync(string ticket, string otp, CancellationToken ct = default)
    {
        var consumed = await _tickets.ConsumeAsync(ticket, "otp", ct);
        if (consumed is null)
            return LoginResult.Fail(LoginOutcome.InvalidCredentials);

        var user = await _users.FindByIdAsync(consumed.UserId, ct);
        if (user is null || !user.IsActive)
            return LoginResult.Fail(LoginOutcome.Disabled);
        if (string.IsNullOrEmpty(user.TotpSecretProtected))
            return LoginResult.Fail(LoginOutcome.InvalidCredentials);

        var code = (otp ?? "").Trim();
        var secret = _protector.Unprotect(user.TotpSecretProtected);

        var amr = "pwd otp";
        var ok = _totp.Verify(secret, code, now: _clock.GetUtcNow());
        if (!ok && await _recovery.ConsumeAsync(user.Id, code, ct))
        {
            ok = true;
            amr = "pwd rc"; // signed in with a recovery code
        }

        if (!ok)
        {
            await _lockouts.RecordFailureAsync(user.Username, ct);
            return LoginResult.Fail(LoginOutcome.InvalidCredentials);
        }

        await _lockouts.ResetAsync(user.Username, ct);
        return await IssueForAsync(user, amr, ct);
    }

    private async Task<LoginResult> IssueForAsync(AuthUser user, string amr, CancellationToken ct)
    {
        var (token, exp) = _access.Issue(user, amr);
        var refresh = await _refresh.IssueAsync(user.Id, ct);
        await _users.SetLastLoginAsync(user.Id, _clock.GetUtcNow(), ct);

        return new LoginResult
        {
            Outcome = LoginOutcome.Success,
            User = user,
            AccessToken = token,
            AccessExpiresUtc = exp,
            RefreshToken = refresh,
            MustChangePassword = user.MustChangePassword,
        };
    }
}
