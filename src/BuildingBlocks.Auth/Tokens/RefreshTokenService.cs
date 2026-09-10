using System.Security.Cryptography;
using BuildingBlocks.Auth.Users;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Auth.Tokens;

/// <summary>A freshly minted refresh token: the <see cref="Raw"/> value to hand to the client, plus its expiry.</summary>
public sealed record IssuedRefreshToken(string Raw, DateTimeOffset ExpiresUtc);

/// <summary>The outcome of presenting a refresh token.</summary>
public enum RefreshOutcome { Ok, Unknown, Expired, Revoked, Reused }

/// <summary>Result of <see cref="RefreshTokenService.RotateAsync"/>.</summary>
public sealed record RefreshResult(RefreshOutcome Outcome, string? UserId, IssuedRefreshToken? Next)
{
    public bool Ok => Outcome == RefreshOutcome.Ok;
}

/// <summary>
/// Issues opaque refresh tokens and rotates them. The raw token is
/// <c>{id}.{secret}</c>; only the SHA-256 of the whole thing is stored. Presenting
/// an already-revoked token is always rejected; with
/// <see cref="AuthOptions.RevokeAllOnRefreshReuse"/> it also revokes every other
/// session for that user (an aggressive theft response that can, under a racy
/// client, log a legitimate user out).
/// </summary>
public sealed class RefreshTokenService
{
    private readonly IRefreshTokenStore _store;
    private readonly AuthOptions _opts;
    private readonly TimeProvider _clock;

    public RefreshTokenService(IRefreshTokenStore store, IOptions<AuthOptions> opts, TimeProvider? clock = null)
    {
        _store = store;
        _opts = opts.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IssuedRefreshToken> IssueAsync(string userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("n");
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var raw = $"{id}.{secret}";
        var expires = now.AddDays(_opts.RefreshTokenDays);

        await _store.AddAsync(new RefreshToken
        {
            Id = id,
            UserId = userId,
            TokenHash = Hash(raw),
            CreatedUtc = now,
            ExpiresUtc = expires,
        }, ct);

        return new IssuedRefreshToken(raw, expires);
    }

    /// <summary>Validate a presented token and, if good, revoke it and issue its replacement.</summary>
    public async Task<RefreshResult> RotateAsync(string rawToken, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var existing = await _store.FindByHashAsync(Hash(rawToken), ct);
        if (existing is null)
            return new RefreshResult(RefreshOutcome.Unknown, null, null);

        if (existing.RevokedUtc is not null)
        {
            // A revoked token being replayed. Always reject it; optionally treat it
            // as theft and drop every other session too.
            if (_opts.RevokeAllOnRefreshReuse)
                await _store.RevokeAllForUserAsync(existing.UserId, now, ct);
            return new RefreshResult(RefreshOutcome.Reused, existing.UserId, null);
        }

        if (now >= existing.ExpiresUtc)
            return new RefreshResult(RefreshOutcome.Expired, existing.UserId, null);

        var next = await IssueAsync(existing.UserId, ct);
        var nextId = next.Raw.Split('.', 2)[0];
        await _store.RevokeAsync(existing.Id, nextId, now, ct);
        return new RefreshResult(RefreshOutcome.Ok, existing.UserId, next);
    }

    /// <summary>Revoke a single token (sign out on this device). Safe if it is unknown or already revoked.</summary>
    public async Task RevokeAsync(string rawToken, CancellationToken ct = default)
    {
        var existing = await _store.FindByHashAsync(Hash(rawToken), ct);
        if (existing is not null)
            await _store.RevokeAsync(existing.Id, null, _clock.GetUtcNow(), ct);
    }

    /// <summary>Revoke every refresh token for a user (sign out everywhere).</summary>
    public Task<int> RevokeAllAsync(string userId, CancellationToken ct = default) =>
        _store.RevokeAllForUserAsync(userId, _clock.GetUtcNow(), ct);

    internal static string Hash(string raw)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
