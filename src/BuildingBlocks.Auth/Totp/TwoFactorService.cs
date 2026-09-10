using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Users;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Totp;

/// <summary>What the client needs to add the account to an authenticator app.</summary>
public sealed record TwoFactorSetup(string Secret, string OtpAuthUri, bool AlreadyEnabled);

public enum TwoFactorConfirmOutcome { Enabled, NoPendingSecret, WrongCode }

public sealed record TwoFactorConfirmResult(
    TwoFactorConfirmOutcome Outcome, IReadOnlyList<string>? RecoveryCodes)
{
    public bool Ok => Outcome == TwoFactorConfirmOutcome.Enabled;
}

public enum TwoFactorDisableOutcome { Disabled, WrongPassword, NotEnabled, UserNotFound }

/// <summary>
/// Enrol, confirm and disable an account's TOTP second factor.
///
/// Enrol is two steps: <see cref="BeginSetupAsync"/> stores the (protected) secret
/// on the account with <see cref="AuthUser.TwoFactorEnabled"/> still false, then
/// <see cref="ConfirmAsync"/> checks a live code and flips it on, handing back a
/// set of recovery codes.
/// </summary>
public sealed class TwoFactorService
{
    private readonly IUserStore _users;
    private readonly TotpService _totp;
    private readonly ITotpSecretProtector _protector;
    private readonly RecoveryCodeService _recovery;
    private readonly IRecoveryCodeStore _recoveryStore;
    private readonly PasswordService _passwords;
    private readonly RefreshTokenService _refresh;
    private readonly AuthOptions _opts;
    private readonly TimeProvider _clock;

    public TwoFactorService(
        IUserStore users, TotpService totp, ITotpSecretProtector protector, RecoveryCodeService recovery,
        IRecoveryCodeStore recoveryStore, PasswordService passwords, RefreshTokenService refresh,
        IOptions<AuthOptions> opts, TimeProvider? clock = null)
    {
        _users = users;
        _totp = totp;
        _protector = protector;
        _recovery = recovery;
        _recoveryStore = recoveryStore;
        _passwords = passwords;
        _refresh = refresh;
        _opts = opts.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Start enrolment. Generates and stores a fresh secret (idempotent while not yet enabled).</summary>
    public async Task<TwoFactorSetup?> BeginSetupAsync(string userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null) return null;

        if (user.TwoFactorEnabled)
            return new TwoFactorSetup("", "", AlreadyEnabled: true);

        var secret = _totp.GenerateSecret();
        await _users.UpdateAsync(user with { TotpSecretProtected = _protector.Protect(secret) }, ct);

        var uri = _totp.BuildOtpAuthUri(_opts.TotpIssuerName, user.Username, secret);
        return new TwoFactorSetup(Group(secret), uri, AlreadyEnabled: false);
    }

    /// <summary>Confirm a code against the pending secret and switch 2FA on.</summary>
    public async Task<TwoFactorConfirmResult> ConfirmAsync(string userId, string code, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null || string.IsNullOrEmpty(user.TotpSecretProtected))
            return new TwoFactorConfirmResult(TwoFactorConfirmOutcome.NoPendingSecret, null);

        if (user.TwoFactorEnabled)
            return new TwoFactorConfirmResult(TwoFactorConfirmOutcome.Enabled, null); // already on; nothing to do

        var secret = _protector.Unprotect(user.TotpSecretProtected);
        if (!_totp.Verify(secret, code ?? "", now: _clock.GetUtcNow()))
            return new TwoFactorConfirmResult(TwoFactorConfirmOutcome.WrongCode, null);

        await _users.UpdateAsync(user with { TwoFactorEnabled = true }, ct);
        var codes = await _recovery.GenerateAsync(user.Id, ct);
        return new TwoFactorConfirmResult(TwoFactorConfirmOutcome.Enabled, codes);
    }

    /// <summary>Turn 2FA off. Requires the account password. Wipes the secret and recovery codes.</summary>
    public async Task<TwoFactorDisableOutcome> DisableAsync(string userId, string currentPassword, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null) return TwoFactorDisableOutcome.UserNotFound;
        if (!user.TwoFactorEnabled) return TwoFactorDisableOutcome.NotEnabled;
        if (!_passwords.Verify(user.PasswordHash, currentPassword ?? ""))
            return TwoFactorDisableOutcome.WrongPassword;

        await _users.UpdateAsync(user with { TwoFactorEnabled = false, TotpSecretProtected = null }, ct);
        await _recoveryStore.ReplaceAllAsync(user.Id, [], ct);       // wipe recovery codes
        await _refresh.RevokeAllAsync(user.Id, ct);                  // force re-auth after a security change
        return TwoFactorDisableOutcome.Disabled;
    }

    private static string Group(string secret) =>
        string.Join(" ", Chunk(secret, 4));

    private static IEnumerable<string> Chunk(string s, int n)
    {
        for (var i = 0; i < s.Length; i += n)
            yield return s.Substring(i, Math.Min(n, s.Length - i));
    }
}
