using MySqlConnector;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Data
{
    public static class Peliculas
    {
        public static async Task<List<Bouquet>> ObtenerPeliculas()
        {
            List<Bouquet> bouquet= new List<Bouquet>();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmdPeliculas = cnn.CreateCommand())
                {
                    if (cnn.State == System.Data.ConnectionState.Closed) 
                        await cnn.OpenAsync();

                    cmdPeliculas.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg FROM streams_tl a " +
                                                            "INNER JOIN stream_categories b " +
                                                            "on a.id_categoria = b.id " +
                                                            "WHERE habilitado=1 and tipo=2 " +
                                                            "order by a.id desc;";

                    using (var reader = cmdPeliculas.ExecuteReader())
                        while (await reader.ReadAsync())
                        {
                            bouquet.Add(new Modelos.Bouquet
                            {
                                Id = reader.GetInt32(0),
                                Nombre = reader.GetString(1),
                                Imagen = reader.GetString(2),
                                Categoria = reader.GetString(3),
                                Tipo = reader.GetInt32(4),
                                Contenedor = reader.GetString(5),
                                CanalEPG = reader.GetString(6),
                            });
                        }
                }
            }

            return bouquet;
        }
    }
}
