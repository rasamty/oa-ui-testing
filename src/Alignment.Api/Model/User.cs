namespace Alignment.Api.Model;

/// <summary>
/// A person who can sign in (Phase 2). Mirrors the <c>users</c> table.
/// <c>username_lower</c> is derived from <see cref="Username"/> in the repository,
/// so it is not a field here.
/// </summary>
public sealed record User
{
    public required string Id { get; init; }
    public required string Username { get; init; }

    /// <summary>Output of <c>PasswordHasher&lt;User&gt;.HashPassword</c>. Never the raw password.</summary>
    public required string PasswordHash { get; init; }

    public required string OrganisationId { get; init; }

    public bool IsActive { get; init; } = true;
    public bool MustChangePassword { get; init; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; init; }
}
