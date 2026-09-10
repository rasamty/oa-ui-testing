using BuildingBlocks.Auth.Data;
using BuildingBlocks.Auth.Login;
using BuildingBlocks.Auth.Passwords;
using BuildingBlocks.Auth.Tokens;
using BuildingBlocks.Auth.Totp;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildingBlocks.Auth.DependencyInjection;

/// <summary>Registration for the auth building block. One call wires the whole library.</summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>Name of the environment variable the JWT signing key is read from. Never appsettings.</summary>
    public const string SigningKeyEnvVar = "AUTH_SIGNING_KEY";

    /// <summary>
    /// Register everything the auth library needs: options (from the <c>Auth</c> section), the
    /// SQLite stores, password hashing, TOTP, lockout tracking, refresh tokens and the login
    /// service. The JWT signing key is read from the <c>AUTH_SIGNING_KEY</c> environment
    /// variable when the token service is first resolved — pass <paramref name="signingKeyOverride"/>
    /// only from tests.
    /// </summary>
    public static IServiceCollection AddBuildingBlocksAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        string? signingKeyOverride = null)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<AuthDatabaseOptions>(configuration.GetSection("Auth:Database"));
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthDatabaseOptions>>().Value;
            return new AuthDatabase(opts);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher<AuthUser>, PasswordHasher<AuthUser>>();
        services.AddSingleton<PasswordService>();
        services.AddSingleton<TotpService>();

        services.AddSingleton<IUserStore, SqliteUserStore>();
        services.AddSingleton<UserProvisioningService>();
        services.AddSingleton<IRefreshTokenStore, SqliteRefreshTokenStore>();
        services.AddSingleton<RefreshTokenService>();
        services.AddSingleton<ILoginAttemptTracker, SqliteLoginAttemptTracker>();
        services.AddSingleton<ILoginTicketStore, SqliteLoginTicketStore>();
        services.AddSingleton<IRecoveryCodeStore, SqliteRecoveryCodeStore>();
        services.AddSingleton<RecoveryCodeService>();
        services.AddSingleton<TwoFactorService>();
        services.AddSingleton<LoginService>();
        services.AddSingleton<PasswordChangeService>();
        services.AddSingleton<UsernameChangeService>();

        // TOTP secrets are protected with ASP.NET data protection when the host has it
        // (every WebApplication does); otherwise a pass-through, so a CLI host still starts.
        services.AddSingleton<ITotpSecretProtector>(sp =>
        {
            var dp = sp.GetService<IDataProtectionProvider>();
            return dp is null ? new NullTotpSecretProtector() : new DataProtectionTotpSecretProtector(dp);
        });

        services.AddSingleton(_ =>
        {
            var raw = signingKeyOverride
                ?? Environment.GetEnvironmentVariable(SigningKeyEnvVar)
                ?? throw new InvalidOperationException(
                    $"The '{SigningKeyEnvVar}' environment variable is not set. The JWT signing key must come " +
                    "from the environment (or a secret store), never configuration files.");
            if (raw.Length < 32)
                throw new InvalidOperationException($"'{SigningKeyEnvVar}' must be at least 32 characters.");
            return AuthSigningKey.FromString(raw);
        });
        services.AddSingleton<AccessTokenService>();

        return services;
    }
}
