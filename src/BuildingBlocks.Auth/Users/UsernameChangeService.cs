using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;

namespace BuildingBlocks.Auth.Users;

public enum UsernameChangeOutcome { Changed, UserNotFound, WrongPassword, PolicyViolation, Taken, Unchanged }

public sealed record UsernameChangeResult(
    UsernameChangeOutcome Outcome,
    string? Error,
    AuthUser? User,
    string? AccessToken,
    DateTimeOffset? AccessExpiresUtc,
    IssuedRefreshToken? RefreshToken)
{
    public bool Ok => Outcome == UsernameChangeOutcome.Changed;
    internal static UsernameChangeResult Fail(UsernameChangeOutcome o, string? error = null) =>
        new(o, error, null, null, null, null);
}

/// <summary>
/// Changes a signed-in user's own username. The stable id (<see cref="AuthUser.Id"/>,
/// the token <c>sub</c>) never moves, so anything keyed by it — the product's rows,
/// refresh tokens, 2FA — is untouched. Only the <c>name</c> claim changes, so the
/// caller is handed a fresh token pair. Other sessions keep working; their tokens
/// just carry the old display name until they next refresh.
/// </summary>
public sealed class UsernameChangeService
{
    private readonly IUserStore _users;
    private readonly PasswordService _passwords;
    private readonly AccessTokenService _access;
    private readonly RefreshTokenService _refresh;

    public UsernameChangeService(
        IUserStore users, PasswordService passwords, AccessTokenService access, RefreshTokenService refresh)
    {
        _users = users;
        _passwords = passwords;
        _access = access;
        _refresh = refresh;
    }

    public async Task<UsernameChangeResult> ChangeAsync(
        string userId, string newUsername, string currentPassword, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId, ct);
        if (user is null)
            return UsernameChangeResult.Fail(UsernameChangeOutcome.UserNotFound);

        if (!_passwords.Verify(user.PasswordHash, currentPassword ?? ""))
            return UsernameChangeResult.Fail(UsernameChangeOutcome.WrongPassword, "Password is wrong.");

        newUsername = (newUsername ?? "").Trim();
        if (string.Equals(newUsername, user.Username, StringComparison.Ordinal))
            return UsernameChangeResult.Fail(UsernameChangeOutcome.Unchanged, "That is already your username.");

        var policy = _passwords.ValidateUsername(newUsername);
        if (policy is not null)
            return UsernameChangeResult.Fail(UsernameChangeOutcome.PolicyViolation, policy);

        // Someone else has it? (A case-only change by the same user is allowed.)
        var clash = await _users.FindByUsernameAsync(newUsername, ct);
        if (clash is not null && clash.Id != user.Id)
            return UsernameChangeResult.Fail(UsernameChangeOutcome.Taken, $"'{newUsername}' is already taken.");

        var updated = user with { Username = newUsername };
        if (!await _users.UpdateAsync(updated, ct))
            return UsernameChangeResult.Fail(UsernameChangeOutcome.Taken, $"'{newUsername}' is already taken.");

        var (token, exp) = _access.Issue(updated, amr: "pwd");
        var refresh = await _refresh.IssueAsync(user.Id, ct);
        return new UsernameChangeResult(UsernameChangeOutcome.Changed, null, updated, token, exp, refresh);
    }
}
