namespace BuildingBlocks.Auth.Endpoints;

// Wire contracts for the auth endpoints. Deliberately flat and boring — a client in
// any language should be able to talk to this without a generated SDK.

public sealed record LoginBody(string Username, string Password);

public sealed record TwoFactorBody(string Ticket, string Code);

public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);

public sealed record ChangeUsernameBody(string NewUsername, string CurrentPassword);

public sealed record ConfirmTwoFactorBody(string Code);
public sealed record DisableTwoFactorBody(string CurrentPassword);

/// <summary>Everything the client needs to add the account to an authenticator app.</summary>
public sealed record TwoFactorSetupResponse(string Secret, string OtpAuthUri, bool AlreadyEnabled);

/// <summary>The recovery codes, shown to the user exactly once.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// What a successful login / refresh returns. The refresh token is ALSO set as an
/// HttpOnly cookie; it is echoed here only so non-browser clients can store it.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessExpiresUtc,
    string TokenType,
    string RefreshToken,
    DateTimeOffset RefreshExpiresUtc,
    bool MustChangePassword,
    MeResponse User);

/// <summary>Returned when the account has 2FA — the client must follow up with the OTP.</summary>
public sealed record TwoFactorRequiredResponse(string Ticket, string Detail = "Two-factor code required.");

public sealed record MeResponse(
    string Id,
    string Username,
    string OrganisationId,
    string Role,
    IReadOnlyList<string> Permissions,
    bool TwoFactorEnabled,
    DateTimeOffset AccessEndsUtc);

// ---- admin API (users.admin) ----

public sealed record AdminUserView(
    string Id,
    string Username,
    string OrganisationId,
    string Role,
    IReadOnlyList<string> Permissions,
    bool IsActive,
    bool MustChangePassword,
    bool TwoFactorEnabled,
    DateTimeOffset AccessStartsUtc,
    DateTimeOffset AccessEndsUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc);

public sealed record AdminCreateUserBody(
    string Username,
    string Password,
    string? OrganisationId,
    string? Role,
    string? Permissions,
    bool MustChangePassword = true,
    int? TrialDays = null);

public sealed record AdminResetPasswordBody(string NewPassword, bool MustChange = true);
public sealed record AdminAccessWindowBody(DateTimeOffset? StartsUtc, DateTimeOffset? EndsUtc);
public sealed record AdminExtendTrialBody(int Days);
