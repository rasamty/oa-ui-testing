using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Totp;
using BuildingBlocks.Auth.Users;

namespace BuildingBlocks.Auth.Admin;

/// <summary>Outcome shared by the admin mutations.</summary>
public enum AdminOutcome { Ok, NotFound, PolicyViolation, NotAllowed }

public sealed record AdminResult(AdminOutcome Outcome, string? Error, AuthUser? User)
{
    public bool Ok => Outcome == AdminOutcome.Ok;
    public static AdminResult Fail(AdminOutcome o, string? error = null) => new(o, error, null);
    public static AdminResult Success(AuthUser user) => new(AdminOutcome.Ok, null, user);
}

/// <summary>
/// Everything the <c>users.admin</c> API does. Each mutation that changes what a
/// user may do — disable, reset password, reset 2FA, revoke sessions — also cuts
/// the token denylist so the change bites on that user's very next request, and
/// drops their refresh tokens so they cannot silently get a new access token.
/// </summary>
public sealed class AdminService
{
    private readonly IUserStore _users;
    private readonly UserProvisioningService _provisioning;
    private readonly PasswordService _passwords;
    private readonly RefreshTokenService _refresh;
    private readonly ITokenDenylist _denylist;
    private readonly IRecoveryCodeStore _recoveryCodes;
    private readonly TimeProvider _clock;

    public AdminService(
        IUserStore users, UserProvisioningService provisioning, PasswordService passwords,
        RefreshTokenService refresh, ITokenDenylist denylist, IRecoveryCodeStore recoveryCodes,
        TimeProvider? clock = null)
    {
        _users = users;
        _provisioning = provisioning;
        _passwords = passwords;
        _refresh = refresh;
        _denylist = denylist;
        _recoveryCodes = recoveryCodes;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<AuthUser>> ListAsync(string? organisationId, CancellationToken ct = default) =>
        _users.ListAsync(organisationId, ct);

    public Task<AuthUser?> GetAsync(string id, CancellationToken ct = default) =>
        _users.FindByIdAsync(id, ct);

    public async Task<(AdminOutcome outcome, string? error, AuthUser? user)> CreateAsync(
        NewUserRequest request, CancellationToken ct = default)
    {
        var result = await _provisioning.CreateAsync(request, ct);
        return result.Ok
            ? (AdminOutcome.Ok, null, result.User)
            : (AdminOutcome.PolicyViolation, result.Error, null);
    }

    public async Task<AdminResult> SetActiveAsync(
        string actingUserId, string targetUserId, bool active, CancellationToken ct = default)
    {
        if (!active && string.Equals(actingUserId, targetUserId, StringComparison.Ordinal))
            return AdminResult.Fail(AdminOutcome.NotAllowed, "You cannot disable your own account.");

        var user = await _users.FindByIdAsync(targetUserId, ct);
        if (user is null) return AdminResult.Fail(AdminOutcome.NotFound);

        var updated = user with { IsActive = active };
        await _users.UpdateAsync(updated, ct);

        if (!active)
            await CutOffAsync(targetUserId, ct);
        else
            await _denylist.ClearAsync(targetUserId, ct);

        return AdminResult.Success(updated);
    }

    public async Task<AdminResult> ResetPasswordAsync(
        string targetUserId, string newPassword, bool mustChange, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(targetUserId, ct);
        if (user is null) return AdminResult.Fail(AdminOutcome.NotFound);

        var policy = _passwords.ValidatePassword(newPassword ?? "", user.Username);
        if (policy is not null) return AdminResult.Fail(AdminOutcome.PolicyViolation, policy);

        var updated = user with { PasswordHash = _passwords.Hash(newPassword!), MustChangePassword = mustChange };
        await _users.UpdateAsync(updated, ct);
        await CutOffAsync(targetUserId, ct);
        return AdminResult.Success(updated);
    }

    public async Task<AdminResult> ResetTwoFactorAsync(string targetUserId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(targetUserId, ct);
        if (user is null) return AdminResult.Fail(AdminOutcome.NotFound);

        var updated = user with { TwoFactorEnabled = false, TotpSecretProtected = null };
        await _users.UpdateAsync(updated, ct);
        await _recoveryCodes.ReplaceAllAsync(targetUserId, Array.Empty<string>(), ct);
        await CutOffAsync(targetUserId, ct);
        return AdminResult.Success(updated);
    }

    public async Task<AdminResult> SetAccessWindowAsync(
        string targetUserId, DateTimeOffset? startsUtc, DateTimeOffset? endsUtc, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(targetUserId, ct);
        if (user is null) return AdminResult.Fail(AdminOutcome.NotFound);

        var starts = startsUtc ?? user.AccessStartsUtc;
        var ends = endsUtc ?? user.AccessEndsUtc;
        if (ends <= starts)
            return AdminResult.Fail(AdminOutcome.PolicyViolation, "The end of the access window must be after its start.");

        var updated = user with { AccessStartsUtc = starts, AccessEndsUtc = ends };
        await _users.UpdateAsync(updated, ct);
        return AdminResult.Success(updated);
    }

    public async Task<AdminResult> ExtendTrialAsync(string targetUserId, int days, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(targetUserId, ct);
        if (user is null) return AdminResult.Fail(AdminOutcome.NotFound);

        var now = _clock.GetUtcNow();
        // Extend from whichever is later: now, or the current end.
        var basis = user.AccessEndsUtc > now ? user.AccessEndsUtc : now;
        var updated = user with { AccessEndsUtc = basis.AddDays(days) };
        await _users.UpdateAsync(updated, ct);
        await _denylist.ClearAsync(targetUserId, ct); // a lapsed trial being re-opened
        return AdminResult.Success(updated);
    }

    public async Task<AdminResult> RevokeSessionsAsync(string targetUserId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(targetUserId, ct);
        if (user is null) return AdminResult.Fail(AdminOutcome.NotFound);
        await CutOffAsync(targetUserId, ct);
        return AdminResult.Success(user);
    }

    public async Task<AdminOutcome> DeleteAsync(string actingUserId, string targetUserId, CancellationToken ct = default)
    {
        if (string.Equals(actingUserId, targetUserId, StringComparison.Ordinal))
            return AdminOutcome.NotAllowed;
        var removed = await _users.DeleteAsync(targetUserId, ct);
        if (!removed) return AdminOutcome.NotFound;
        await _denylist.ClearAsync(targetUserId, ct); // tidy — the rows cascade anyway
        return AdminOutcome.Ok;
    }

    private async Task CutOffAsync(string userId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await _denylist.DenyBeforeAsync(userId, now, ct);
        await _refresh.RevokeAllAsync(userId, ct);
    }
}
