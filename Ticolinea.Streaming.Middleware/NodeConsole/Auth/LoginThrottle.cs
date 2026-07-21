using System.Collections.Concurrent;

namespace ticolinea.stream.service.NodeConsole.Auth;

/// <summary>
/// Counts failed console logins and locks a caller out after
/// <see cref="MaxAttempts"/> failures for <see cref="LockoutWindow"/>.
///
/// State is in-process, not in the DB: a node is a single process, the data is
/// worthless across restarts, and an operator-initiated restart clearing a
/// lockout is acceptable. Keyed per (username, client IP) rather than per
/// username, so an opportunistic scanner hammering "admin" cannot lock the real
/// owner out of their own node.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public int Failures;
        public DateTime FirstFailureUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public static string KeyFor(string username, string? ip) =>
        $"{username.Trim().ToLowerInvariant()}|{ip ?? "?"}";

    public bool IsLocked(string key, DateTime nowUtc, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(key, out var e)) return false;

        var unlocksAt = e.FirstFailureUtc + LockoutWindow;
        if (e.Failures < MaxAttempts) return false;

        if (nowUtc >= unlocksAt)
        {
            // Expired: drop it entirely rather than leaving the counter at the
            // limit, which would re-lock on the very next failed attempt.
            _entries.TryRemove(key, out _);
            return false;
        }

        retryAfter = unlocksAt - nowUtc;
        return true;
    }

    /// <summary>Records a failure. Returns true only for the attempt that crosses into lockout.</summary>
    public bool RecordFailure(string key, DateTime nowUtc)
    {
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry { Failures = 1, FirstFailureUtc = nowUtc },
            (_, e) =>
            {
                // The window is measured from the first failure; once it has
                // elapsed the run is stale and counting restarts.
                if (nowUtc >= e.FirstFailureUtc + LockoutWindow)
                {
                    e.Failures = 1;
                    e.FirstFailureUtc = nowUtc;
                }
                else
                {
                    e.Failures++;
                }
                return e;
            });

        return entry.Failures == MaxAttempts;
    }

    public void RecordSuccess(string key) => _entries.TryRemove(key, out _);
}
