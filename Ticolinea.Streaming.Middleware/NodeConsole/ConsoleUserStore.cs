using MySqlConnector;
using ticolinea.stream.service.NodeConsole.Auth;

namespace ticolinea.stream.service.NodeConsole;

public static class ConsoleUserStore
{
    private const string Columns = "id, username, display_name, role, enabled, is_seed, last_login";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private static ConsoleUser Read(MySqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Username = r.GetString(1),
        DisplayName = r.GetString(2),
        Role = r.GetString(3),
        Enabled = r.GetBoolean(4),
        IsSeed = r.GetBoolean(5),
        LastLogin = r.IsDBNull(6) ? null : r.GetDateTime(6),
    };

    public static async Task<List<ConsoleUser>> ListAsync()
    {
        var list = new List<ConsoleUser>();
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM node_admin_users ORDER BY is_seed DESC, username ASC;";
        await using var r = (MySqlDataReader)await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(Read(r));
        return list;
    }

    /// <summary>Verifies credentials and opens a session. Null = rejected.</summary>
    public static async Task<(ConsoleUser User, string RawToken)?> LoginAsync(string username, string password)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();

        string hash;
        ConsoleUser user;
        await using (var cmd = cnn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {Columns}, password_hash FROM node_admin_users WHERE username = @u;";
            cmd.Parameters.AddWithValue("@u", username);
            await using var r = (MySqlDataReader)await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            user = Read(r);
            hash = r.GetString(7);
        }

        // Disabled accounts fail identically to a wrong password: the response
        // must not tell an attacker which usernames exist.
        if (!user.Enabled || !PasswordHasher.Verify(password, hash)) return null;

        var token = SessionToken.New();
        await using (var cmd = cnn.CreateCommand())
        {
            cmd.CommandText = @"
INSERT INTO node_admin_sessions (token_hash, user_id, created_utc, expires_utc)
VALUES (@t, @u, UTC_TIMESTAMP(), @e);";
            cmd.Parameters.AddWithValue("@t", token.Hash);
            cmd.Parameters.AddWithValue("@u", user.Id);
            cmd.Parameters.AddWithValue("@e", DateTime.UtcNow.Add(SessionLifetime));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = cnn.CreateCommand())
        {
            cmd.CommandText = "UPDATE node_admin_users SET last_login = UTC_TIMESTAMP() WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", user.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        return (user, token.Raw);
    }

    /// <summary>Resolves a raw cookie token to its user, or null if the session is dead.</summary>
    public static async Task<ConsoleUser?> ResolveSessionAsync(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = $@"
SELECT u.id, u.username, u.display_name, u.role, u.enabled, u.is_seed, u.last_login, s.expires_utc
FROM node_admin_sessions s
INNER JOIN node_admin_users u ON u.id = s.user_id
WHERE s.token_hash = @t;";
        cmd.Parameters.AddWithValue("@t", SessionToken.HashFor(rawToken));

        await using var r = (MySqlDataReader)await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        var user = Read(r);
        // Re-checked per request, so disabling a user cuts them off at once.
        return SessionPolicy.IsValid(r.GetDateTime(7), user.Enabled, DateTime.UtcNow) ? user : null;
    }

    public static async Task LogoutAsync(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "DELETE FROM node_admin_sessions WHERE token_hash = @t;";
        cmd.Parameters.AddWithValue("@t", SessionToken.HashFor(rawToken));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Null when the username is taken.</summary>
    public static async Task<ConsoleUser?> CreateAsync(NewUserInput input)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();

        var username = input.Username!.Trim().ToLowerInvariant();
        var role = string.Equals(input.Role, "owner", StringComparison.OrdinalIgnoreCase) ? "owner" : "operator";
        var display = string.IsNullOrWhiteSpace(input.DisplayName) ? username : input.DisplayName!.Trim();

        try
        {
            await using var cmd = cnn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO node_admin_users (username, display_name, password_hash, role, enabled, is_seed, created_at)
VALUES (@u, @d, @h, @r, 1, 0, UTC_TIMESTAMP());
SELECT LAST_INSERT_ID();";
            cmd.Parameters.AddWithValue("@u", username);
            cmd.Parameters.AddWithValue("@d", display);
            cmd.Parameters.AddWithValue("@h", PasswordHasher.Hash(input.Password!));
            cmd.Parameters.AddWithValue("@r", role);
            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            return new ConsoleUser { Id = id, Username = username, DisplayName = display, Role = role, Enabled = true, IsSeed = false };
        }
        catch (MySqlException ex) when (ex.Number == 1062) // duplicate key
        {
            return null;
        }
    }

    /// <summary>False when the target is the seed account, which must stay reachable.</summary>
    public static async Task<bool> SetEnabledAsync(int id, bool enabled)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        // Locking out the bootstrap account would leave the node with no way in,
        // so the guard lives in SQL rather than trusting the caller.
        cmd.CommandText = "UPDATE node_admin_users SET enabled = @e WHERE id = @id AND is_seed = 0;";
        cmd.Parameters.AddWithValue("@e", enabled);
        cmd.Parameters.AddWithValue("@id", id);
        var affected = await cmd.ExecuteNonQueryAsync();

        // Revoking access must also kill sessions already open.
        if (affected > 0 && !enabled)
        {
            await using var kill = cnn.CreateCommand();
            kill.CommandText = "DELETE FROM node_admin_sessions WHERE user_id = @id;";
            kill.Parameters.AddWithValue("@id", id);
            await kill.ExecuteNonQueryAsync();
        }
        return affected > 0;
    }

    public static async Task<bool> SetPasswordAsync(int id, string password)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = "UPDATE node_admin_users SET password_hash = @h WHERE id = @id;";
        cmd.Parameters.AddWithValue("@h", PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue("@id", id);
        var affected = await cmd.ExecuteNonQueryAsync();

        // A password change ends every other session for that user.
        if (affected > 0)
        {
            await using var kill = cnn.CreateCommand();
            kill.CommandText = "DELETE FROM node_admin_sessions WHERE user_id = @id;";
            kill.Parameters.AddWithValue("@id", id);
            await kill.ExecuteNonQueryAsync();
        }
        return affected > 0;
    }
}
