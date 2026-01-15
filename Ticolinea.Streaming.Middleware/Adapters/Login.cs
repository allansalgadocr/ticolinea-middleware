using MySqlConnector;

namespace ticolinea.stream.service.Adapters;

public static class Login
{
    #region LoginAdmin

    public static async Task<List<Modelos.LoginAdmin>> ObtenerListaAdmin()
    {
        List<Modelos.LoginAdmin> logins = new List<Modelos.LoginAdmin>();

        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "SELECT id_login_admin, usuario, password, fecha_creacion, habilitado " +
                                  "from login_admin;";

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        logins.Add(new Modelos.LoginAdmin
                        {
                            IdLoginAdmin = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            Usuario = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Password = "",
                            FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                            Habilitado = reader.IsDBNull(4) ? (short)0 : reader.GetInt16(4),
                        });
                    }
                }
            }
        }

        return logins;
    }

    public static async Task<Modelos.LoginAdmin> ObtenerLoginAdmin(string usuario)
    {
        Modelos.LoginAdmin login = null;

        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "SELECT id_login_admin, usuario, password, fecha_creacion, habilitado " +
                                  "from login_admin WHERE usuario=@usuario;";

                cmd.Parameters.AddWithValue("@usuario", usuario.Trim().ToLower());

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        login = new Modelos.LoginAdmin
                        {
                            IdLoginAdmin = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            Usuario = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Password = reader.GetString(2),
                            FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                            Habilitado = reader.IsDBNull(4) ? (short)0 : reader.GetInt16(4),
                        };
                    }
                }
            }
        }

        return login;
    }

    public static async Task AgregarUsuarioAsync(Modelos.LoginAdmin login)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "INSERT INTO `login_admin` " +
                                  "(`id_login_admin`,`usuario`,`password`,`fecha_creacion`,`habilitado`) " +
                                  "VALUES(@idLoginAdmin,@usuario,@password,@fecha_creacion,@habilitado); ";

                string idLoginAdmin = Guid.NewGuid().ToString();

                cmd.Parameters.AddWithValue("@idLoginAdmin", idLoginAdmin);
                cmd.Parameters.AddWithValue("@usuario", login.Usuario);
                cmd.Parameters.AddWithValue("@password", login.Password);
                cmd.Parameters.AddWithValue("@fecha_creacion", login.FechaCreacion);
                cmd.Parameters.AddWithValue("@habilitado", 1);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task InactivarUsuarioAsync(string idLoginAdmin)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "UPDATE `login_admin` " +
                                  "SET Habilitado=0 " +
                                  "WHERE id_login_admin=@idLoginAdmin; ";

                cmd.Parameters.AddWithValue("@idLoginAdmin", idLoginAdmin);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task ActivarUsuarioAsync(string idLoginAdmin)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "UPDATE `login_admin` " +
                                  "SET Habilitado=1 " +
                                  "WHERE id_login_admin=@idLoginAdmin; ";

                cmd.Parameters.AddWithValue("@idLoginAdmin", idLoginAdmin);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task CambiarClaveUsuarioAsync(string idLoginAdmin, string password)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "UPDATE `login_admin` " +
                                  "SET Password=@password " +
                                  "WHERE id_login_admin=@idLoginAdmin; ";

                cmd.Parameters.AddWithValue("@idLoginAdmin", idLoginAdmin);
                cmd.Parameters.AddWithValue("@password", password);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    #endregion

    #region LoginTecnicos

    public static async Task<List<Modelos.LoginTecnico>> ObtenerListaTecnicos()
    {
        List<Modelos.LoginTecnico> logins = new List<Modelos.LoginTecnico>();

        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "SELECT id_login_admin, usuario, password, fecha_creacion, habilitado " +
                                  "from login_tecnico;";

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        logins.Add(new Modelos.LoginTecnico
                        {
                            IdLoginTecnico = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            Usuario = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Password = "",
                            FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                            Habilitado = reader.IsDBNull(4) ? (short)0 : reader.GetInt16(4),
                        });
                    }
                }
            }
        }

        return logins;
    }

    public static async Task<Modelos.LoginTecnico> ObtenerLoginTecnico(string usuario)
    {
        Modelos.LoginTecnico login = null;

        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "SELECT id_login_tecnico, usuario, password, fecha_creacion, habilitado " +
                                  "from login_tecnico WHERE usuario=@usuario;";

                cmd.Parameters.AddWithValue("@usuario", usuario.Trim().ToLower());

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        login = new Modelos.LoginTecnico
                        {
                            IdLoginTecnico = reader.IsDBNull(0) ? "" : reader.GetString(0),
                            Usuario = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Password = reader.GetString(2),
                            FechaCreacion = reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3),
                            Habilitado = reader.IsDBNull(4) ? (short)0 : reader.GetInt16(4),
                        };
                    }
                }
            }
        }

        return login;
    }

    public static async Task AgregarLoginTecnicoAsync(Modelos.LoginTecnico login)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "INSERT INTO `login_tecnico` " +
                                  "(`id_login_tecnico`,`usuario`,`password`,`fecha_creacion`,`habilitado`) " +
                                  "VALUES(@idLoginTecnico,@usuario,@password,@fecha_creacion,@habilitado); ";

                string idLoginTecnico = Guid.NewGuid().ToString();

                cmd.Parameters.AddWithValue("@idLoginTecnico", idLoginTecnico);
                cmd.Parameters.AddWithValue("@usuario", login.Usuario);
                cmd.Parameters.AddWithValue("@password", login.Password);
                cmd.Parameters.AddWithValue("@fecha_creacion", login.FechaCreacion);
                cmd.Parameters.AddWithValue("@habilitado", 1);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task InactivarLoginTecnicoAsync(string idLoginAdmin)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "UPDATE `login_tecnico` " +
                                  "SET Habilitado=0 " +
                                  "WHERE id_login_admin=@idLoginAdmin; ";

                cmd.Parameters.AddWithValue("@idLoginAdmin", idLoginAdmin);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task ActivarLoginTecnicoAsync(string idLoginTecnico)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "UPDATE `login_tecnico` " +
                                  "SET Habilitado=1 " +
                                  "WHERE id_login_tecnico=@idLoginTecnico; ";

                cmd.Parameters.AddWithValue("@idLoginTecnico", idLoginTecnico);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task CambiarClaveLoginTecnicoAsync(string idLoginTecnico, string password)
    {
        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();

                cmd.CommandText = "UPDATE `login_tecnico` " +
                                  "SET Password=@password " +
                                  "WHERE id_login_tecnico=@idLoginTecnico; ";

                cmd.Parameters.AddWithValue("@idLoginAdmin", idLoginTecnico);
                cmd.Parameters.AddWithValue("@password", password);

                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    #endregion
}