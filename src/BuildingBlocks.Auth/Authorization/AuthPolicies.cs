using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Auth.Authorization;

/// <summary>
/// Turns the access token's <c>perm</c> claims into named authorization policies.
///
/// A permission <c>"state.write"</c> becomes the policy <c>"perm:state.write"</c>.
/// The policy passes when the caller is authenticated AND either is in the
/// <c>Admin</c> role or carries that exact <c>perm</c> claim. Nothing here hits the
/// database — the token IS the authorization data.
/// </summary>
public static class AuthPolicies
{
    /// <summary>Well-known permission strings the library's own admin API uses.</summary>
    public const string StateRead = "state.read";
    public const string StateWrite = "state.write";
    public const string UsersAdmin = "users.admin";

    /// <summary>The role that bypasses every permission check.</summary>
    public const string AdminRole = "Admin";

    private const string PolicyPrefix = "perm:";

    /// <summary>The policy name for a permission — pass this to <c>RequireAuthorization(...)</c>.</summary>
    public static string Policy(string permission) => PolicyPrefix + permission;

    /// <summary>
    /// Register a <c>perm:*</c> policy for each permission. The library's three are
    /// always included; pass any product-specific ones as <paramref name="permissions"/>.
    /// </summary>
    public static IServiceCollection AddBuildingBlocksAuthorization(
        this IServiceCollection services, params string[] permissions)
    {
        var all = new HashSet<string>(permissions ?? Array.Empty<string>())
        {
            StateRead, StateWrite, UsersAdmin,
        };

        services.AddAuthorization(options =>
        {
            foreach (var permission in all)
            {
                var p = permission;
                options.AddPolicy(Policy(p), builder => builder
                    .RequireAuthenticatedUser()
                    .RequireAssertion(ctx =>
                        ctx.User.IsInRole(AdminRole) ||
                        ctx.User.HasClaim("perm", p)));
            }
        });

        return services;
    }
}
