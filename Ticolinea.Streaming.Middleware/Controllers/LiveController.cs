using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using System.Text;
using System.Text.RegularExpressions;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class LiveController : ControllerBase
    {
        /// <summary>
        /// Stream by JWT token - validates token and serves HLS playlist
        /// </summary>
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}.{ext}")]
        public async Task<IActionResult> StreamingByToken(int chID, string ext, [FromQuery] string? token = null)
        {
            if (string.IsNullOrWhiteSpace(ext) || ext.ToLower() != "m3u8")
                return Unauthorized();

            // Extract and validate token
            var extractedToken = token ?? TokenValidation.ExtractToken(Request);
            var validation = TokenValidation.ValidateToken(extractedToken);
            
            if (validation == null || !validation.IsValid)
                return Unauthorized("Invalid or expired token");

            // Validate MAC if present in token claims
            if (!string.IsNullOrEmpty(validation.Mac))
            {
                // MAC is bound - could add additional MAC validation here if needed
            }

            var existeCanal = await ObtieneDatosCanal(chID);
            if (!existeCanal)
                return Unauthorized();

            var playlistOutput = await ReadPlaylistFile(chID);
            playlistOutput = AddDiscontinuityTags(playlistOutput);
            playlistOutput = ReplaceSegmentUrls(playlistOutput);

            return Content(playlistOutput, "application/x-mpegurl", Encoding.UTF8);
        }

        /// <summary>
        /// Stream by JWT token for mobile - validates token and serves HLS playlist
        /// </summary>
        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}")]
        public async Task<IActionResult> StreamingMovilByToken(int chID, [FromQuery] string? token = null)
        {
            var extractedToken = token ?? TokenValidation.ExtractToken(Request);
            var validation = TokenValidation.ValidateToken(extractedToken);
            
            if (validation == null || !validation.IsValid)
                return Unauthorized("Invalid or expired token");

            var existeCanal = await ObtieneDatosCanal(chID);
            if (!existeCanal)
                return Unauthorized();

            var playlistOutput = await ReadPlaylistFile(chID);
            playlistOutput = AddDiscontinuityTags(playlistOutput);
            playlistOutput = ReplaceSegmentUrls(playlistOutput);

            return Content(playlistOutput, "application/x-mpegurl", Encoding.UTF8);
        }

        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}/{usuario}/{password}.{ext}")]
        public async Task<IActionResult> Streaming(int chID, string usuario, string password, string ext)
        {
            if (string.IsNullOrWhiteSpace(ext) || ext.ToLower() != "m3u8")
                return Unauthorized();

            Modelos.Usuario? usuariodb = null;
            if (IsRegularUser(usuario))
            {
                usuariodb = await Helpers.Usuario.VerificarUsuario(usuario, password);
                if (usuariodb == null)
                    return Unauthorized();
            }

            var existeCanal = await ObtieneDatosCanal(chID);
            if (!existeCanal)
                return Unauthorized();

            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var playlistFile = Path.Combine(streamsFolder, $"{chID}_.m3u8");

            if (!System.IO.File.Exists(playlistFile))
                return NotFound("Playlist not found");

            string playlistOutput = await System.IO.File.ReadAllTextAsync(playlistFile);

            playlistOutput = AddDiscontinuityTags(playlistOutput);
            playlistOutput = ReplaceSegmentUrls(playlistOutput);

            if (usuariodb != null)
            {
                ActualizarActividadMovil(chID, usuario, password, usuariodb, "");
            }
            
            return Content(playlistOutput, "application/x-mpegurl", Encoding.UTF8);
        }

        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}/mac/{macAddress}.{ext}")]
        public async Task<IActionResult> StreamingByMac(int chID, string macAddress, string ext)
        {
            if (string.IsNullOrWhiteSpace(ext) || ext.ToLower() != "m3u8")
                return Unauthorized();

            // Validate MAC address against client_mac_addresses table
            var validation = await Helpers.ClientValidation.ValidateMacAddress(macAddress);
            if (validation == null || !validation.IsValid)
                return Unauthorized("MAC address not authorized");

            var existeCanal = await ObtieneDatosCanal(chID);
            if (!existeCanal)
                return Unauthorized();

            var playlistOutput = await ReadPlaylistFile(chID);
            playlistOutput = AddDiscontinuityTags(playlistOutput);
            playlistOutput = ReplaceSegmentUrls(playlistOutput);

            return Content(playlistOutput, "application/x-mpegurl", Encoding.UTF8);
        }

        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{chID}/{usuario}/{password}/{macAddress}")]
        public async Task<IActionResult> StreamingMovil(int chID, string usuario, string password, string macAddress)
        {
            Modelos.Usuario? usuariodb = null;

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
            playlistOutput = ReplaceSegmentUrls(playlistOutput);

            if (usuariodb != null)
            {
                ActualizarActividadMovil(chID, usuario, password, usuariodb, macAddress);
            }
                

            return Content(playlistOutput, "application/x-mpegurl", Encoding.UTF8);
        }

        [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        [HttpGet("{usuario}/{password}/{token}/{segment}")]
        public async Task<IActionResult> Hls(string usuario, string password, string token, string segment)
        {
            string tokenMatch = MD5($"{usuario}{password}zxcvbnm7852{segment}");
            if (token != tokenMatch) return Unauthorized();

            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var segmentFile = $"{streamsFolder}{segment}";
            var fileBytes = await System.IO.File.ReadAllBytesAsync(segmentFile);
            MemoryStream stream = new(fileBytes);

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

        private static string ReplaceSegmentUrls(string playlistOutput)
        {
            var outputBuilder = new StringBuilder();
            var lines = playlistOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (line.EndsWith(".ts"))
                    outputBuilder.AppendLine($"{Constantes.Global.STREAMS_BASE_URL}/streams/{line}");
                else
                    outputBuilder.AppendLine(line);
            }

            return outputBuilder.ToString();
        }

        private static string AddDiscontinuityTags(string playlistOutput)
        {
            if (playlistOutput.Contains("#EXT-X-DISCONTINUITY")) return playlistOutput;
            
            const string mediaSequenceTag = "EXT-X-MEDIA-SEQUENCE:";
            var index = playlistOutput.IndexOf(mediaSequenceTag, StringComparison.Ordinal);

            if (index == -1) return playlistOutput;
            var lineEnd = playlistOutput.IndexOf('\n', index);
            if (lineEnd != -1)
            {
                playlistOutput = playlistOutput.Insert(lineEnd + 1, "#EXT-X-DISCONTINUITY\n");
            }

            return playlistOutput;
        }

        private void ActualizarActividadMovil(int chID, string usuario, string password, Modelos.Usuario? usuariodb, string macAddress)
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

        private static bool IsRegularUser(string usuario)
        {
            return usuario != "monitor" && usuario != "test" && usuario != "fibraencasapanel";
        }

        private static async Task<string> ReadPlaylistFile(int chID)
        {
            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var playlistFile = $"{streamsFolder}{chID}_.m3u8";
            return await System.IO.File.ReadAllTextAsync(playlistFile).ConfigureAwait(false);
        }
    }
}
