namespace BuildingBlocks.Auth.Login;

/// <summary>Current lockout state for a key (a username, lower-cased).</summary>
public sealed record LockoutState(bool IsLockedOut, DateTimeOffset? LockedOutUntil, int FailedCount);

/// <summary>
/// Counts failed sign-ins per username and reports when one is locked out.
/// Deliberately keyed by username, not IP — a shared office IP should not lock a
/// whole building out, and an attacker rotating IPs should still be slowed.
/// </summary>
public interface ILoginAttemptTracker
{
    Task<LockoutState> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Record a failed attempt. Returns the state after recording (may now be locked out).</summary>
    Task<LockoutState> RecordFailureAsync(string key, CancellationToken ct = default);

    /// <summary>Clear the counter after a successful sign-in.</summary>
    Task ResetAsync(string key, CancellationToken ct = default);
}
