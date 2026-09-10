using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Users;

namespace BuildingBlocks.Auth.Passwords;

public enum PasswordChangeOutcome { Ok, UserNotFound, WrongCurrentPassword, PolicyViolation }

/// <summary>Result of a password change. On <see cref="PasswordChangeOutcome.Ok"/> a fresh token pair is included.</summary>
public sealed record PasswordChangeResult(
    PasswordChangeOutcome Outcome,
    string? Error,
    AuthUser? User,
    string? AccessToken,
    DateTimeOffset? AccessExpiresUtc,
    IssuedRefreshToken? RefreshToken)
{
    public bool Ok => Outcome == PasswordChangeOutcome.Ok;
    internal static PasswordChangeResult Fail(PasswordChangeOutcome o, string? error = null) =>
        new(o, error, null, null, null, null);
}

/// <summary>
/// Changes a signed-in user's own password: verify the current one, run the policy,
/// store the new hash and clear <see cref="AuthUser.MustChangePassword"/>. Every
/// other session is dropped (all refresh tokens revoked) and the caller is handed a
/// fresh token pair so the change does not sign them out.
/// </summary>
public sealed class PasswordChangeService
{
    private readonly IUserStore _users;
    private readonly PasswordService _passwords;
    private readonly AccessTokenService _access;
    private readonly RefreshTokenService _refresh;

    public PasswordChangeService(
        IUserStore users, PasswordService passwords, AccessTokenService access, RefreshTokenService refresh)
    {
        _users = users;
        _passwords = passwords;
        _access = access;
        _refresh = refresh;
    }

    public async Task<PasswordChangeResult> ChangeAsync(
        string userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null)
            return PasswordChangeResult.Fail(PasswordChangeOutcome.UserNotFound);

        if (!_passwords.Verify(user.PasswordHash, currentPassword ?? ""))
            return PasswordChangeResult.Fail(PasswordChangeOutcome.WrongCurrentPassword, "Current password is wrong.");

        var policy = _passwords.ValidatePassword(newPassword ?? "", user.Username);
        if (policy is not null)
            return PasswordChangeResult.Fail(PasswordChangeOutcome.PolicyViolation, policy);

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
            return PasswordChangeResult.Fail(PasswordChangeOutcome.PolicyViolation, "New password must be different.");

        var updated = user with { PasswordHash = _passwords.Hash(newPassword!), MustChangePassword = false };
        await _users.UpdateAsync(updated, ct);

        await _refresh.RevokeAllAsync(user.Id, ct);
        var (token, exp) = _access.Issue(updated, amr: "pwd");
        var refresh = await _refresh.IssueAsync(user.Id, ct);

        return new PasswordChangeResult(PasswordChangeOutcome.Ok, null, updated, token, exp, refresh);
    }
}
