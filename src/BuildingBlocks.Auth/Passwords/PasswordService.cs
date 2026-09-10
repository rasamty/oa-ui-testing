using System.Text.RegularExpressions;
using BuildingBlocks.Auth.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Passwords;

/// <summary>
/// Hashes and verifies passwords (PBKDF2, via ASP.NET's <see cref="PasswordHasher{TUser}"/>)
/// and enforces the configured password / username policy. The one place either policy lives.
/// </summary>
public sealed class PasswordService
{
    private readonly IPasswordHasher<AuthUser> _hasher;
    private readonly AuthOptions _opts;
    private readonly Regex _usernameRegex;

    public PasswordService(IPasswordHasher<AuthUser> hasher, IOptions<AuthOptions> opts)
    {
        _hasher = hasher;
        _opts = opts.Value;
        _usernameRegex = new Regex(_opts.Username.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    public string Hash(string password) => _hasher.HashPassword(user: null!, password);

    /// <summary>True if the password matches. (Rehash-needed is treated as a match — upgrade lazily on the next change.)</summary>
    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(user: null!, hash, password) != PasswordVerificationResult.Failed;

    /// <summary>Null when acceptable, otherwise the reason to show the user.</summary>
    public string? ValidatePassword(string password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _opts.Password.MinLength)
            return $"password must be at least {_opts.Password.MinLength} characters";
        if (_opts.Password.MustDifferFromUsername &&
            string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            return "password must not be the username";
        return null;
    }

    /// <summary>Null when acceptable, otherwise the reason.</summary>
    public string? ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "username is required";
        if (username.Length < _opts.Username.MinLength || username.Length > _opts.Username.MaxLength)
            return $"username must be {_opts.Username.MinLength}-{_opts.Username.MaxLength} characters";
        if (!_usernameRegex.IsMatch(username))
            return "username may only contain letters, digits, dot, dash and underscore";
        return null;
    }
}
