using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using System.Text;
using System.Text.RegularExpressions;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class LiveController : ControllerBase
    {

        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}/{usuario}/{password}.{ext}")]
        public async Task<IActionResult> Streaming(int chID, string usuario, string password, string ext)
        {
            if (ext == null)
                return Unauthorized();

            if (ext != "m3u8")
                return Unauthorized();

            Usuario usuariodb = null;
            if ((usuario != "monitor" && password != "monitor") ||
                (usuario != "test" && password != "test"))
            {
                usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password).ConfigureAwait(false);

                if (usuariodb == null)
                    return Unauthorized();
            }


            var existeCanal = await ObtieneDatosCanal(chID);
            if (!existeCanal)
                return Unauthorized();

            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var playlistFile = $"{streamsFolder}{chID}_.m3u8";
            string playlistOutput = await System.IO.File.ReadAllTextAsync(playlistFile);

            string pattern = @"(.*?).ts";
            Regex rg = new(pattern);
            var matches = rg.Matches(playlistOutput);
            if (!playlistOutput.Contains("EXT-X-DISCONTINUITY"))
            {
                string patternTest = @"(EXT-X-MEDIA-SEQUENCE:[0-9]*\n)";
                Regex rgtest = new(patternTest);
                var matchestest = rgtest.Matches(playlistOutput);
                foreach (var match in matchestest)
                {
                    playlistOutput = playlistOutput.Replace(match.ToString(), $@"{match}#EXT-X-DISCONTINUITY{Environment.NewLine}");
                }
            }

            foreach (var match in matches)
            {
                string token = MD5($"{usuario}{password}zxcvbnm7852{match}");
                playlistOutput = playlistOutput.Replace(match.ToString(), $@"/Live/Hls/{usuario}/{password}/{token}/{match}");
            }

            byte[] byteArray = Encoding.UTF8.GetBytes(playlistOutput);
            MemoryStream stream = new(byteArray);

            var ip = "";
            if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
                ip = forwardedIps.First();

            var userAgent = Request.Headers["User-Agent"].ToString();

            if ((usuario != "monitor" && password != "monitor") &&
                (usuario != "test" && password != "test") &&
                (usuario != "fibraencasapanel"))
            {
                _ = Helpers.Usuario.ActualizaInfoUsuario(usuariodb?.UsuarioId ?? 0, chID, userAgent, ip, usuariodb?.ConexionesMaximas ?? 0);
            }

            return File(stream, "application/x-mpegurl", $"{chID}.m3u8");
        }

        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}/{usuario}/{password}/{macAddress}")]
        public async Task<IActionResult> StreamingMovil(int chID, string usuario, string password,string macAddress)
        {
            Usuario usuariodb = null;

            if (IsRegularUser(usuario))
            {
                usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);

                if (usuariodb == null)
                    return Unauthorized();
            }


            var existeCanal = await ObtieneDatosCanal(chID);
            if (!existeCanal)
                return Unauthorized();

            var playlistOutput = await ReadPlaylistFile(chID);
            playlistOutput = AddDiscontinuityTags(playlistOutput);
            playlistOutput = ReplaceSegmentUrls(playlistOutput, usuario, password);

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlistOutput));

            ActualizarActividadMovil(chID, usuario, password, usuariodb, macAddress);

            return File(stream, "application/x-mpegurl", $"{chID}.m3u8");
        }

        [ResponseCache(Duration = 10, Location = ResponseCacheLocation.Any, NoStore = false)]
        [HttpGet("{usuario}/{password}/{token}/{segment}")]
        public async Task<IActionResult> Hls(string usuario, string password, string token, string segment)
        {
            string tokenMatch = MD5($"{usuario}{password}zxcvbnm7852{segment}");
            if (token != tokenMatch) return Unauthorized();

            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var segmentFile = $"{streamsFolder}{segment}";
            var fileBytes = await System.IO.File.ReadAllBytesAsync(segmentFile);
            MemoryStream stream = new(fileBytes);

            //return PhysicalFile(segmentFile, "video/mp2t");

            return File(stream, "video/mp2t", segment);
        }


        private async Task<bool> ObtieneDatosCanal(int chnId)
        {
            string ubicacionStreams = Constantes.Global.STREAMS_FOLDER;

            try
            {
                List<StreamDb> streams = new();

                using (var conn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        if (conn.State == System.Data.ConnectionState.Closed) await conn.OpenAsync();
                        cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate, cgop, gop FROM streams_tl a " +
                                        "INNER JOIN streams_info b " +
                                        "on a.id = b.stream_id " +
                                        $"WHERE iniciado = 1 AND stream_id = {chnId} and Habilitado=1;";

                        using (var reader = await cmd.ExecuteReaderAsync())
                            while (await reader.ReadAsync())
                            {
                                streams.Add(new StreamDb
                                {
                                    Fuente = reader.GetString(0),
                                    StreamId = reader.GetInt32(1),
                                    ProbeSize = reader.GetInt32(2),
                                    EsBajoDemanda = reader.GetInt32(3),
                                    ProcesoId = reader.GetInt32(4),
                                    TranscodeAudio = reader.GetString(5),
                                    Intervalo = reader.GetInt16(6),
                                    Segmentos = reader.GetInt16(7),
                                    Framerate = reader.GetInt32(8),
                                    Transcode = reader.GetInt32(9),
                                    Resolucion = reader.GetString(10),
                                    Bitrate = reader.GetString(11),
                                    CGOP = reader.GetInt32(12),
                                    GOP = reader.GetInt32(13),
                                });
                            }
                    }
                }


                var stream = streams.FirstOrDefault();

                if (stream == null)
                {
                    Console.WriteLine("Canal no encontrado");
                    return false;
                };


                if (stream.ProcesoId > -1)
                {
                    bool EstaCorriendoStream = await Jobs.ObtenerProcesoFFMPEG(stream.ProcesoId, stream.StreamId);
                    if ((!EstaCorriendoStream && stream.EsBajoDemanda == 1) || (!EstaCorriendoStream && stream.EsBajoDemanda == 0))
                    {
                        Console.WriteLine($"Canal {chnId} sin proceso, iniciando stream");
                        //Inicia stream
                        await Jobs.IniciarStream(stream);
                        await Task.Delay(400);

                        bool archivoExiste = false;
                        int ciclo = 0;
                        while (archivoExiste == false && ciclo < 50)
                        {
                            archivoExiste = System.IO.File.Exists($"{ubicacionStreams}{stream.StreamId}_.m3u8");
                            ciclo++;
                            await Task.Delay(400);
                        }

                        return archivoExiste;
                    }
                    else
                        return true;
                }
                else
                {
                    Console.WriteLine($"Canal {chnId} sin proceso, iniciado stream");

                    //Inicia stream
                    await Jobs.IniciarStream(stream);
                    await Task.Delay(400);

                    bool archivoExiste = false;
                    int ciclo = 0;
                    while (archivoExiste == false && ciclo < 45)
                    {
                        archivoExiste = System.IO.File.Exists($"{ubicacionStreams}{stream.StreamId}_.m3u8");
                        ciclo++;
                        await Task.Delay(400);
                    }

                    return archivoExiste;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ObtieneDatosCanal] ERROR AL OBTENER DATA STREAM." + ex.Message);
            }

            return false;
        }

        private static string MD5(string s)
        {
            using var provider = System.Security.Cryptography.MD5.Create();
            StringBuilder builder = new();

            foreach (byte b in provider.ComputeHash(Encoding.UTF8.GetBytes(s)))
                builder.Append(b.ToString("x2").ToLower());

            return builder.ToString();
        }

        private string ReplaceSegmentUrls(string playlistOutput, string usuario, string password)
        {
            string pattern = @"(.*?).ts";
            Regex rg = new(pattern);
            var matches = rg.Matches(playlistOutput);

            foreach (var match in matches)
            {
                string token = MD5($"{usuario}{password}zxcvbnm7852{match}");
                playlistOutput = playlistOutput.Replace(match.ToString(), $@"/Live/Hls/{usuario}/{password}/{token}/{match}");
            }

            return playlistOutput;
        }

        private string AddDiscontinuityTags(string playlistOutput)
        {
            if (!playlistOutput.Contains("EXT-X-DISCONTINUITY"))
            {
                string patternTest = @"(EXT-X-MEDIA-SEQUENCE:[0-9]*\n)";
                Regex rgtest = new(patternTest);
                var matchestest = rgtest.Matches(playlistOutput);
                foreach (var match in matchestest)
                {
                    playlistOutput = playlistOutput.Replace(match.ToString(), $@"{match}#EXT-X-DISCONTINUITY{Environment.NewLine}");
                }
            }

            return playlistOutput;
        }

        private void ActualizarActividadMovil(int chID, string usuario, string password, Usuario usuariodb, string macAddress)
        {
            var ip = "";
            var userAgent = Request.Headers["User-Agent"].ToString();

            if (Request.Headers.TryGetValue("X-Real-IP", out var forwardedIps))
                ip = forwardedIps.First();

            if (IsRegularUser(usuario) && usuariodb != null)
            {
                _ = Helpers.Usuario.ActualizarActividadMovil(usuariodb?.UsuarioId ?? 0, chID, userAgent, ip, macAddress, 1);
            }
        }

        private bool IsRegularUser(string usuario)
        {
            return usuario != "monitor" && usuario != "test" && usuario != "fibraencasapanel";
        }

        private async Task<string> ReadPlaylistFile(int chID)
        {
            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var playlistFile = $"{streamsFolder}{chID}_.m3u8";
            return await System.IO.File.ReadAllTextAsync(playlistFile).ConfigureAwait(false);
        }
    }
}
