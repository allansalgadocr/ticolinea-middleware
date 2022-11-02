using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using ticolinea.stream.service.Db;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class SeriesController : ControllerBase
    {
        [HttpGet("{usuario}/{password}")]
        public async Task<IActionResult> Obtener(string usuario, string password)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            List<Modelos.Serie> series = new();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "select a.id,a.caratula,a.genero, a.temporadas,a.titulo,a.caratula_grande,b.stream_id,b.resena resena_episodio,a.resena resena_serie,c.contenedor,c.nombre_stream,b.temporada_num,a.fechaLanzamiento from series_info a " +
                                        "inner join series_episodios b " +
                                        "on a.id = b.serie_id inner join streams_tl c " +
                                        "on b.stream_id = c.id " +
                                        "WHERE c.habilitado = 1 " +
                                        "order by a.titulo asc, a.genero asc, b.orden asc; ";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            series.Add(new Modelos.Serie
                            {
                                SerieId = reader.GetInt32(0),
                                Imagen = reader.GetString(1),
                                Genero = reader.GetString(2),
                                Temporadas = reader.GetString(3),
                                Titulo = reader.GetString(4),
                                ImagenGrande = reader.GetString(5),
                                StreamId = reader.GetInt32(6),
                                URL = $"http://tv.play-latino.com:27701/Peliculas/Reproducir/{reader.GetInt32(6)}/{usuario}/{password}.{reader.GetString(9)}",
                                ResenaEpisodio = reader.GetValue(7)?.ToString(),
                                ResenaSerie = reader.GetValue(8)?.ToString(),
                                TituloEpisodio = reader.GetString(10),
                                TemporadaNum = reader.GetInt32(11),
                                FechaLanzamiento = reader.GetString(12)
                            });
                        }
                }
            }

            return Ok(series);
        }

        [HttpGet("{usuario}/{password}/{serieId}")]
        public async Task<IActionResult> ObtenerEpisodios(string usuario, string password, int serieId)
        {
            var usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            Modelos.EpisodioRequest episodioRequest = new();
            episodioRequest.Episodios = new();

            using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
            {
                using (var cmd = cnn.CreateCommand())
                {
                    if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                    cmd.CommandText = "select a.episodio_num,a.serie_id,a.orden,a.temporada_num,b.imagen_stream,b.contenedor,a.resena,b.nombre_stream,b.id from series_episodios a " +
                                        "inner join streams_tl b " +
                                        "on a.stream_id = b.id " +
                                        $"where serie_id = {serieId} and b.habilitado=1; ";

                    using (var reader = await cmd.ExecuteReaderAsync())
                        while (await reader.ReadAsync())
                        {
                            episodioRequest.Episodios.Add(new Modelos.Episodio
                            {
                                EpisodioNum = reader.GetInt32(0),
                                SerieId = reader.GetInt32(1),
                                Orden = reader.GetInt32(2),
                                TemporadaNum = reader.GetInt32(3),
                                Imagen = reader.GetString(4),
                                Contenedor = reader.GetString(5),
                                Resena = reader.GetString(6),
                                Titulo = reader.GetString(7),
                                URL = $"http://tv.play-latino.com:27701/Peliculas/Reproducir/{reader.GetInt32(8)}/{usuario}/{password}.{reader.GetString(5)}"
                            });
                        }
                }
            }

            if (episodioRequest.Episodios.Any())
            {
                episodioRequest.Temporadas = episodioRequest.Episodios.Select(s => s.TemporadaNum).Distinct().ToList();
            }

            return Ok(episodioRequest);
        }
    }
}
