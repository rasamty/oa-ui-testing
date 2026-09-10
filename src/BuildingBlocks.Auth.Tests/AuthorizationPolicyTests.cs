using System.Security.Claims;
using BuildingBlocks.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Auth.Tests;

public class AuthorizationPolicyTests
{
    private static IAuthorizationService BuildAuthz(params string[] extra)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBuildingBlocksAuthorization(extra);
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal User(string? role = null, params string[] perms)
    {
        var claims = new List<Claim>();
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        claims.AddRange(perms.Select(p => new Claim("perm", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test", nameType: "name", roleType: ClaimTypes.Role));
    }

    [Fact]
    public async Task A_matching_perm_claim_passes_the_policy()
    {
        var authz = BuildAuthz();
        var result = await authz.AuthorizeAsync(User(perms: "state.write"), AuthPolicies.Policy(AuthPolicies.StateWrite));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_missing_perm_claim_fails_the_policy()
    {
        var authz = BuildAuthz();
        var readOnly = User(perms: "state.read");
        Assert.True((await authz.AuthorizeAsync(readOnly, AuthPolicies.Policy(AuthPolicies.StateRead))).Succeeded);
        Assert.False((await authz.AuthorizeAsync(readOnly, AuthPolicies.Policy(AuthPolicies.StateWrite))).Succeeded);
    }

    [Fact]
    public async Task The_Admin_role_bypasses_every_permission()
    {
        var authz = BuildAuthz();
        var admin = User(role: AuthPolicies.AdminRole); // no perm claims at all
        Assert.True((await authz.AuthorizeAsync(admin, AuthPolicies.Policy(AuthPolicies.StateWrite))).Succeeded);
        Assert.True((await authz.AuthorizeAsync(admin, AuthPolicies.Policy(AuthPolicies.UsersAdmin))).Succeeded);
    }

    [Fact]
    public async Task An_unauthenticated_principal_fails()
    {
        var authz = BuildAuthz();
        var anon = new ClaimsPrincipal(new ClaimsIdentity()); // not authenticated
        Assert.False((await authz.AuthorizeAsync(anon, AuthPolicies.Policy(AuthPolicies.StateRead))).Succeeded);
    }

    [Fact]
    public async Task Extra_product_permissions_get_their_own_policy()
    {
        var authz = BuildAuthz("reports.export");
        Assert.True((await authz.AuthorizeAsync(User(perms: "reports.export"), AuthPolicies.Policy("reports.export"))).Succeeded);
    }
}
