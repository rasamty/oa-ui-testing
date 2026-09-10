using Microsoft.AspNetCore.DataProtection;

namespace BuildingBlocks.Auth.Totp;

/// <summary>
/// Wraps / unwraps a user's TOTP secret before it is stored. The secret is as
/// sensitive as the password hash, so it is never written in the clear.
/// </summary>
public interface ITotpSecretProtector
{
    string Protect(string base32Secret);
    string Unprotect(string protectedValue);
}

/// <summary>
/// Default protector — ASP.NET <see cref="IDataProtector"/>. The keys live wherever
/// the host has pointed data protection (on this deployment, the persistent
/// <c>/home</c> disk), so protected secrets survive a restart.
/// </summary>
public sealed class DataProtectionTotpSecretProtector : ITotpSecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionTotpSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("BuildingBlocks.Auth.Totp.v1");

    public string Protect(string base32Secret) => _protector.Protect(base32Secret);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

/// <summary>Pass-through protector for tests and hosts that have not configured data protection.</summary>
public sealed class NullTotpSecretProtector : ITotpSecretProtector
{
    public string Protect(string base32Secret) => base32Secret;
    public string Unprotect(string protectedValue) => protectedValue;
}
