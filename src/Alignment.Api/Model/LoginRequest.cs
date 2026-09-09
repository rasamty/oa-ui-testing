namespace Alignment.Api.Model;

/// <summary>Body of <c>POST /api/auth/login</c>. Lower-case names match the JSON the page sends.</summary>
public sealed record LoginRequest(string? username, string? password);

/// <summary>Body of <c>POST /api/auth/change-password</c> (authenticated).</summary>
public sealed record ChangePasswordRequest(string? currentPassword, string? newPassword);
