using Alignment.Api.Model;

namespace Alignment.Api.Data;

/// <summary>
/// Storage for sign-in accounts (Phase 2). Separate from <see cref="IStateRepository"/>
/// so board code and account code stay independent. Phase 2 keeps every user in the
/// base ("demo") database file — login has to find a user before it knows their
/// organisation, so users cannot be sharded per-org. Phase 3 revisits this.
/// </summary>
public interface IUserRepository
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>Insert a new user. Returns false if the username is already taken.</summary>
    Task<bool> CreateAsync(User user, CancellationToken ct = default);

    /// <summary>Look a user up for login. Case-insensitive on the username.</summary>
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);

    Task<User?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Stamp <c>last_login_utc</c> after a successful sign-in.</summary>
    Task SetLastLoginAsync(string id, CancellationToken ct = default);

    /// <summary>Replace the password hash and clear <c>must_change_password</c>.</summary>
    Task SetPasswordAsync(string id, string newPasswordHash, CancellationToken ct = default);

    /// <summary>Enable or disable an account. Returns false if the username is unknown.</summary>
    Task<bool> SetActiveAsync(string username, bool active, CancellationToken ct = default);
}
