using BuildingBlocks.Auth.Passwords;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Users;

/// <summary>What the caller supplies to create an account. Everything else is derived or defaulted.</summary>
public sealed record NewUserRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string OrganisationId { get; init; }
    public string Role { get; init; } = "Member";
    public string Permissions { get; init; } = "";
    public bool MustChangePassword { get; init; } = true;

    /// <summary>Explicit trial window. When null, the account gets <see cref="AuthOptions.DefaultTrialDays"/> from now.</summary>
    public DateTimeOffset? AccessStartsUtc { get; init; }
    public DateTimeOffset? AccessEndsUtc { get; init; }
}

/// <summary>Outcome of a provisioning attempt.</summary>
public sealed record ProvisionResult(bool Ok, string? Error, AuthUser? User)
{
    public static ProvisionResult Fail(string error) => new(false, error, null);
    public static ProvisionResult Success(AuthUser user) => new(true, null, user);
}

/// <summary>
/// Turns a <see cref="NewUserRequest"/> into a stored <see cref="AuthUser"/>: runs the
/// username / password policy, hashes the password, applies the trial window, writes it.
/// The one correct way to make an account — the CLI, the admin API and first-run
/// bootstrap all go through here.
/// </summary>
public sealed class UserProvisioningService
{
    private readonly IUserStore _store;
    private readonly PasswordService _passwords;
    private readonly AuthOptions _opts;
    private readonly TimeProvider _clock;

    public UserProvisioningService(
        IUserStore store, PasswordService passwords, IOptions<AuthOptions> opts, TimeProvider? clock = null)
    {
        _store = store;
        _passwords = passwords;
        _opts = opts.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ProvisionResult> CreateAsync(NewUserRequest req, CancellationToken ct = default)
    {
        var nameError = _passwords.ValidateUsername(req.Username);
        if (nameError is not null) return ProvisionResult.Fail(nameError);

        var pwError = _passwords.ValidatePassword(req.Password, req.Username);
        if (pwError is not null) return ProvisionResult.Fail(pwError);

        if (string.IsNullOrWhiteSpace(req.OrganisationId))
            return ProvisionResult.Fail("organisation is required");

        var now = _clock.GetUtcNow();
        var user = new AuthUser
        {
            Id = Guid.NewGuid().ToString(),
            Username = req.Username,
            PasswordHash = _passwords.Hash(req.Password),
            OrganisationId = req.OrganisationId,
            Role = req.Role,
            Permissions = req.Permissions,
            IsActive = true,
            MustChangePassword = req.MustChangePassword,
            AccessStartsUtc = req.AccessStartsUtc ?? now,
            AccessEndsUtc = req.AccessEndsUtc ?? now.AddDays(_opts.DefaultTrialDays),
            CreatedUtc = now,
        };

        var result = await _store.CreateAsync(user, ct);
        return result == UserCreateResult.Created
            ? ProvisionResult.Success(user)
            : ProvisionResult.Fail($"username '{req.Username}' is already taken");
    }
}
