namespace BuildingBlocks.Auth.Users;

/// <summary>The outcome of a <see cref="IUserStore.CreateAsync"/> call.</summary>
public enum UserCreateResult
{
    Created = 0,
    /// <summary>The username (case-insensitive) is already taken.</summary>
    UsernameTaken = 1,
}

/// <summary>
/// Storage for sign-in accounts. The library ships one implementation
/// (<see cref="SqliteUserStore"/>); a host can supply its own without touching
/// anything else in the library.
/// </summary>
/// <remarks>
/// Mutations other than create/delete go through <see cref="UpdateAsync"/>: load
/// the record, produce a modified copy with <c>with { … }</c>, and save it. This
/// keeps the surface small — "disable", "reset password", "extend trial" and the
/// rest of the admin API are all just field edits on <see cref="AuthUser"/>.
/// </remarks>
public interface IUserStore
{
    /// <summary>Create the <c>auth_*</c> tables if they do not exist. Safe to call repeatedly.</summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>Find an account for login. Case-insensitive on the username. Null if unknown.</summary>
    Task<AuthUser?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Find an account by its stable id (the token <c>sub</c>). Null if unknown.</summary>
    Task<AuthUser?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Every account, or every account in one organisation. Ordered by username.</summary>
    Task<IReadOnlyList<AuthUser>> ListAsync(string? organisationId = null, CancellationToken ct = default);

    /// <summary>Insert a new account. Returns <see cref="UserCreateResult.UsernameTaken"/> on a name clash.</summary>
    Task<UserCreateResult> CreateAsync(AuthUser user, CancellationToken ct = default);

    /// <summary>
    /// Overwrite every mutable column of the account with <see cref="AuthUser.Id"/> equal to
    /// <paramref name="user"/>.Id. Returns false if no such account exists. The id and
    /// <c>created_utc</c> are never changed.
    /// </summary>
    Task<bool> UpdateAsync(AuthUser user, CancellationToken ct = default);

    /// <summary>Remove an account and everything that cascades from it. Returns false if it was not there.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Stamp <c>last_login_utc</c>. Cheap, called on the hot path after a successful sign-in.</summary>
    Task SetLastLoginAsync(string id, DateTimeOffset whenUtc, CancellationToken ct = default);
}
