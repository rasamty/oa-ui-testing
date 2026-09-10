using Microsoft.AspNetCore.Authentication;

namespace BuildingBlocks.Auth.Authentication;

/// <summary>Adds the library's bearer scheme to an <see cref="AuthenticationBuilder"/>.</summary>
public static class AuthenticationBuilderExtensions
{
    /// <summary>
    /// Register the <c>BuildingBlocksBearer</c> scheme — validates
    /// <c>Authorization: Bearer &lt;jwt&gt;</c> against the library's own token service.
    /// Make it the default scheme with
    /// <c>AddAuthentication(BearerAuthenticationHandler.SchemeName)</c>.
    /// </summary>
    public static AuthenticationBuilder AddBuildingBlocksBearer(
        this AuthenticationBuilder builder, string scheme = BearerAuthenticationHandler.SchemeName)
        => builder.AddScheme<BearerAuthenticationOptions, BearerAuthenticationHandler>(scheme, _ => { });
}
