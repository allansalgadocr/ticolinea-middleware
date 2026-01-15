using MySqlConnector;

namespace ticolinea.stream.service.Adapters;

public class Bouquet
{
    public static async Task<List<Modelos.Bouquet>> ObtenerLista()
    {
        List<Modelos.Bouquet> bouquet = new();
        List<Modelos.Bouquet> bouquetCustom = new();

        using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
        {
            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                cmd.CommandText =
                    "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg FROM streams a " +
                    "INNER JOIN categorias b " +
                    "on a.id_categoria = b.id " +
                    "WHERE habilitado=1 and tipo=1 and canal_id=0 and mostrar_en_playlist=1 " +
                    "order by a.orden asc;";

                using (var reader = await cmd.ExecuteReaderAsync())
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

            using (var cmd = cnn.CreateCommand())
            {
                if (cnn.State == System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                cmd.CommandText =
                    "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg, canal_id FROM streams a " +
                    "INNER JOIN categorias b " +
                    "on a.id_categoria = b.id " +
                    "WHERE habilitado=1 and tipo=1 and canal_id != 0  and mostrar_en_playlist=1 " +
                    "order by a.canal_id asc;";

                using (var reader = await cmd.ExecuteReaderAsync())
                    while (await reader.ReadAsync())
                    {
                        bouquetCustom.Add(new Modelos.Bouquet
                        {
                            Id = reader.GetInt32(0),
                            Nombre = reader.GetString(1),
                            Imagen = reader.GetString(2),
                            Categoria = reader.GetString(3),
                            Tipo = reader.GetInt32(4),
                            Contenedor = reader.GetString(5),
                            CanalEPG = reader.GetString(6),
                            CanalId = reader.GetInt32(7)
                        });
                    }
            }

            foreach (var canal in bouquetCustom)
            {
                if (canal.CanalId < bouquet.Count() - 1)
                {
                    bouquet.Insert(canal.CanalId - 1, canal);
                }
                else
                {
                    bouquet.Add(canal);
                }
            }
        }

        return bouquet;
    }
}