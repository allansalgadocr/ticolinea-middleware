using System.Security.Cryptography;
using System.Text;

namespace ticolinea.stream.service.NodeConsole.Auth;

// An opaque bearer token, not a JWT. Chosen deliberately: the console must be
// able to cut off a disabled user immediately, and a self-validating JWT stays
// good until it expires. The lookup cost is irrelevant here — a node has a
// handful of admins, not a traffic tier.
public readonly struct SessionToken
{
    public string Raw { get; }
    public string Hash { get; }

    private SessionToken(string raw, string hash)
    {
        Raw = raw;
        Hash = hash;
    }

    public static SessionToken New()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new SessionToken(raw, HashFor(raw));
    }

    // Only this hash is persisted. A stolen database gives an attacker nothing
    // replayable, and the comparison is a plain equality on an indexed column.
    // Unsalted SHA-256 is correct here (unlike passwords): the input is already
    // 256 bits of entropy, so there is no dictionary to precompute.
    public static string HashFor(string raw)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    // Cookie-safe: no '+', '/' or '=' to be re-encoded or truncated in transit.
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class SessionPolicy
{
    // Both conditions are re-checked on every request, so revoking access is a
    // single UPDATE on the user row — no session sweep required.
    public static bool IsValid(DateTime expiresUtc, bool userEnabled, DateTime now) =>
        userEnabled && expiresUtc > now;
}
