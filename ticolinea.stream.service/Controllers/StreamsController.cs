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
                cmd.CommandText = "SELECT a.id,nombre_stream,imagen_stream,b.category_name,tipo,contenedor, canal_epg FROM streams_tl a " +
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
                            Contenedor = reader.GetString(5),
                            CanalEPG= reader.GetString(6),
                        });
                    }

                cmd.Connection?.Close();
            }

            foreach (var chn in bouquet)
            {
                sb.AppendLine($"#EXTINF:-1 tvg-id=\"{chn.CanalEPG}\" tvg-name=\"{chn.Nombre}\" tvg-logo=\"{chn.Imagen}\" group-title=\"{chn.Categoria}\",{chn.Nombre}\r\n");
                if (chn.Tipo == 1)
                    sb.AppendLine($"http://15.235.50.124:27701/Live/Streaming/{chn.Id}/{usuario}/{password}.m3u8\r\n");
                if (chn.Tipo == 2)
                    sb.AppendLine($"http://15.235.50.124:27701/Peliculas/Reproducir/{chn.Id}/{usuario}/{password}.{chn.Contenedor}\r\n");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "application/x-mpegurl", $"ticolineaplay_{usuario}.m3u");
        }

        [HttpGet("{usuario}/{password}/{anno}/{mes}/{dia}")]
        public IActionResult Informacion(string usuario, string password, int anno, int mes, int dia)
        {
            var usuariodb = Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            List<Modelos.InfoStream> infoStreams = new();
            using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
            {
                string fechaHoraActual = new DateTime(anno, mes, dia).ToString("yyyyMMdd");
                string fechaHoraDiaSiguiente = new DateTime(anno, mes, dia).AddDays(1).ToString("yyyyMMdd");
                string fechaHoraInicio = fechaHoraActual + "0000";
                string fechaHoraFin = fechaHoraDiaSiguiente + "0000";
                var cmd = mariadb.Conexion.CreateCommand();

                cmd.CommandText = "select canal_epg,titulo,descripcion,anno,fecha_hora_inicio,fecha_hora_fin from epg_tl " +
                                  $"where fecha_hora_inicio>= {fechaHoraInicio} and fecha_hora_fin<= {fechaHoraFin}; ";

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        infoStreams.Add(new Modelos.InfoStream
                        {
                            CanalEpg = reader.GetString(0),
                            Titulo = reader.GetString(1),
                            Descripcion = reader.GetString(2),
                            Anno = reader.GetString(3),
                            Inicio = reader.GetInt64(4),
                            Fin = reader.GetInt64(5)
                        });
                    }
                cmd.Connection?.Close();
            }

            return Ok(infoStreams);
        }
    }
}
