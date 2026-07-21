using MySqlConnector;
using ticolinea.stream.service.NodeConsole.Auth;

namespace ticolinea.stream.service.NodeConsole;

// The console's own tables are created by the node, not by the panel's EF
// migration script. They are node-local by definition — console logins never
// exist on the panel — so shipping them through the cross-repo schema.sql
// pipeline would couple two release cycles for no benefit.
// CREATE TABLE IF NOT EXISTS keeps this safe to run on every boot.
public static class ConsoleSchema
{
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(ConsoleSchema));

    public static async Task EnsureAsync(string? seedPassword)
    {
        await using var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN);
        await cnn.OpenAsync();

        await Exec(cnn, @"
CREATE TABLE IF NOT EXISTS node_admin_users (
  id            INT AUTO_INCREMENT PRIMARY KEY,
  username      VARCHAR(32)  NOT NULL,
  display_name  VARCHAR(80)  NOT NULL,
  password_hash VARCHAR(255) NOT NULL,
  role          VARCHAR(16)  NOT NULL DEFAULT 'operator',
  enabled       TINYINT(1)   NOT NULL DEFAULT 1,
  is_seed       TINYINT(1)   NOT NULL DEFAULT 0,
  last_login    DATETIME     NULL,
  created_at    DATETIME     NOT NULL,
  UNIQUE KEY uq_node_admin_users_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

        // token_hash is the PK: every authenticated request is a single primary-key
        // lookup, and the raw token is never stored anywhere.
        await Exec(cnn, @"
CREATE TABLE IF NOT EXISTS node_admin_sessions (
  token_hash  CHAR(64) NOT NULL PRIMARY KEY,
  user_id     INT      NOT NULL,
  created_utc DATETIME NOT NULL,
  expires_utc DATETIME NOT NULL,
  KEY idx_node_admin_sessions_user (user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

        await SeedFirstAdminAsync(cnn, seedPassword);
    }

    // Bootstrap account. Created only when the table is empty, so a redeploy can
    // never resurrect it or reset a password the owner has since changed.
    private static async Task SeedFirstAdminAsync(MySqlConnection cnn, string? seedPassword)
    {
        await using (var count = cnn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM node_admin_users;";
            if (Convert.ToInt32(await count.ExecuteScalarAsync()) > 0) return;
        }

        var password = ResolveSeedPassword(seedPassword, out var generated);

        await using var insert = cnn.CreateCommand();
        insert.CommandText = @"
INSERT INTO node_admin_users (username, display_name, password_hash, role, enabled, is_seed, created_at)
VALUES ('admin', 'Administrador', @hash, 'owner', 1, 1, UTC_TIMESTAMP());";
        insert.Parameters.AddWithValue("@hash", PasswordHasher.Hash(password));
        await insert.ExecuteNonQueryAsync();

        if (generated)
        {
            _log.Warn("=================================================================");
            _log.Warn($"  Console bootstrap account created: admin / {password}");
            _log.Warn("  Store it now — it is not recoverable and will not be logged again.");
            _log.Warn("=================================================================");
        }
        else
        {
            _log.Info("Console bootstrap account 'admin' created from configured NodeConsole:SeedPassword.");
        }
    }

    /// <summary>
    /// Decides the bootstrap password: the configured one when it is usable,
    /// otherwise a freshly generated strong value. <paramref name="generated"/>
    /// tells the caller whether it must be printed to the log.
    ///
    /// A configured-but-too-short value is REFUSED rather than honoured: the
    /// console is password-authenticated on a public port, and a weak known
    /// credential there is worse than a strong unknown one. Bootstrap validates
    /// this too, but appsettings can be hand-edited on the box, so the node does
    /// not trust what it is handed.
    /// </summary>
    public static string ResolveSeedPassword(string? configured, out bool generated)
    {
        var candidate = (configured ?? "").Trim();
        generated = candidate.Length < ConsoleValidation.MinPassword;
        // Base64url from 18 random bytes — 24 chars, no shell/JSON metacharacters.
        return generated ? SessionToken.New().Raw[..24] : candidate;
    }

    private static async Task Exec(MySqlConnection cnn, string sql)
    {
        await using var cmd = cnn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
