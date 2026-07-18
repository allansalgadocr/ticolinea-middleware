using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using System.Text;
using System.Text.RegularExpressions;
using ticolinea.stream.service.Db;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Modelos;
using ticolinea.stream.service.Services;

namespace ticolinea.stream.service.Controllers
{
    [Route("[controller]/[action]")]
    [ApiController]
    public class LiveController : ControllerBase
    {
        private readonly ActivityTrackingService _activityTracker;

        public LiveController(ActivityTrackingService activityTracker)
        {
            _activityTracker = activityTracker;
        }

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

            _activityTracker.TrackIfNeeded(validation, chID, Request, isMobile: false);

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

            _activityTracker.TrackIfNeeded(validation, chID, Request, isMobile: true);

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

            _activityTracker.TrackIfNeeded(validation, chID, macAddress, Request, isMobile: false);

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

        #region Fibraencasa

        [ResponseCache(Duration = 10, Location = ResponseCacheLocation.Any, NoStore = false)]
        [HttpGet("{macaddress}/{token}/{segment}")]
        public async Task<IActionResult> Chunks(string macaddress, string token, string segment)
        {
            string tokenMatch = Helpers.MD5.Encriptar($"{macaddress}zxcvbnm7852{segment}");
            if (!string.Equals(token, tokenMatch, StringComparison.OrdinalIgnoreCase))
                return Unauthorized();

            var segmentFile = $"{Constantes.Global.STREAMS_FOLDER}{segment}";
            byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(segmentFile);
            MemoryStream stream = new(fileBytes);

            return File(stream, "video/mp2t", segment);
        }

        #endregion

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
                                    Fuente = reader.IsDBNull(0) ? "" : reader.GetString(0),
                                    StreamId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                                    ProbeSize = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                                    EsBajoDemanda = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                    ProcesoId = reader.IsDBNull(4) ? -1 : reader.GetInt32(4),
                                    TranscodeAudio = reader.IsDBNull(5) ? "" : reader.GetString(5),
                                    Intervalo = reader.IsDBNull(6) ? (short)4 : reader.GetInt16(6),
                                    Segmentos = reader.IsDBNull(7) ? (short)3 : reader.GetInt16(7),
                                    Framerate = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                                    Transcode = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                                    Resolucion = reader.IsDBNull(10) ? "" : reader.GetString(10),
                                    Bitrate = reader.IsDBNull(11) ? "" : reader.GetString(11),
                                    CGOP = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                                    GOP = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
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

            var subpath = Constantes.Global.PROVIDER_ID == "fibraencasa" ? "/media/" : "/streams/";
            foreach (var line in lines)
            {
                if (line.EndsWith(".ts"))
                    outputBuilder.AppendLine($"{Constantes.Global.STREAMS_BASE_URL}{subpath}{line}");
                else
                    outputBuilder.AppendLine(line);
            }

            return outputBuilder.ToString();
        }

        private static string AddDiscontinuityTags(string playlistOutput)
        {
            // Piloto: con FFmpeg manejando discontinuidades (discont_start+append_list, ver
            // StreamingService), la inyección app-side es redundante y puede forzar resets
            // de decoder en dispositivos estrictos. Flag apagado (default) = comportamiento actual.
            if (Constantes.Global.FFMPEG_MANAGED_DISCONTINUITIES) return playlistOutput;

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
