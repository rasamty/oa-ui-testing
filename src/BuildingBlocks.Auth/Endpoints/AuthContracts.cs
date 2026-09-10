namespace BuildingBlocks.Auth.Endpoints;

// Wire contracts for the auth endpoints. Deliberately flat and boring — a client in
// any language should be able to talk to this without a generated SDK.

public sealed record LoginBody(string Username, string Password);

public sealed record TwoFactorBody(string Ticket, string Code);

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
