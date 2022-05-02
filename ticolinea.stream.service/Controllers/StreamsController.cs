using Microsoft.AspNetCore.Mvc;
using System.Text;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class StreamsController : ControllerBase
    {

        /*[HttpGet]
        public IActionResult Reordenar()
        {
            string orden = "";
            var cmd = this.Mariadb.Conexion.CreateCommand() as MySqlCommand;
            cmd.CommandText = "SELECT replace(replace(bouquet_channels,'[',''),']','') FROM bouquets WHERE Id=1;";

            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                {
                    orden = reader.GetString(0);
                }

            var lista = orden.Split(',');
            var ord = 0;
            foreach (var lst in lista)
            {
                ord++;
                int streamId = 0;
                using (var command = new MySqlCommand($"SELECT id FROM streams_tl WHERE Id={lst};", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        streamId = reader.GetInt32(0);
                    }

                string queryUpdate = "UPDATE streams_tl " +
                               "SET orden=@ord " +
                               "WHERE id=@chnId;";

                using var cmdUpdate = new MySqlCommand(queryUpdate, connection);
                cmdUpdate.Parameters.AddWithValue("@ord", ord);
                cmdUpdate.Parameters.AddWithValue("@chnId", streamId);

                cmdUpdate.ExecuteNonQuery();
            }

            return Ok();
        }*/

        [HttpGet("{usuario}/{password}")]
        public IActionResult Playlist(string usuario, string password)
        {
            var usuariodb = Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            List<Modelos.Bouquet> bouquet = new();
            StringBuilder sb = new();
            sb.AppendLine("#EXTM3U\r\n");
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                var cmd = mariadb.Conexion.CreateCommand();
                cmd.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor FROM streams_tl a " +
                                                        "INNER JOIN stream_categories b " +
                                                        "on a.id_categoria = b.id " +
                                                        "WHERE habilitado=1 " +
                                                        "order by a.orden asc;";

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        bouquet.Add(new Modelos.Bouquet
                        {
                            Id = reader.GetInt32(0),
                            Nombre = reader.GetString(1),
                            Imagen = reader.GetString(2),
                            Categoria = reader.GetString(3),
                            Tipo = reader.GetInt32(4),
                            Contenedor = reader.GetString(5)
                        });
                    }

                cmd.Connection?.Close();
            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.Id}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                    sb.AppendLine($"http://15.235.50.124:27701/Live/Streaming/{chn.Id}/{usuario}/{password}.m3u8\r\n");
                if (chn.Tipo == 2)
                    sb.AppendLine($"http://15.235.50.124:27701/Peliculas/Reproducir/{chn.Id}/{usuario}/{password}.{chn.Contenedor}\r\n");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_{usuario}.m3u");
        }

    }
}
