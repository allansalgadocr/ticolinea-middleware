using System.Security.Cryptography;

namespace ticolinea.stream.service.NodeConsole.Auth;

// PBKDF2-SHA256. Deliberately no new package dependency: the node ships to a
// client's box and every added transitive dependency is another thing to audit
// and patch there.
//
// Format: pbkdf2$<iterations>$<base64 salt>$<base64 hash>
// Self-describing on purpose — iterations can be raised later and old hashes
// still verify against the count they were created with.
public static class PasswordHasher
{
    private const int Iterations = 210_000; // OWASP 2023 floor for PBKDF2-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // Never throws: a corrupt or truncated hash column is an authentication
    // failure, not a 500 that would let a caller distinguish "bad password"
    // from "bad row".
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
