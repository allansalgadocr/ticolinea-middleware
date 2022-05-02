using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        [HttpGet("{chID}/{usuario}/{password}.{ext}")]
        public IActionResult Streaming(int chID, string usuario, string password, string ext)
        {
            if (ext == null) return Unauthorized();
            if (ext != "m3u8") return Unauthorized();

            var usuariodb = Helpers.Usuario.VerificarUsuario(usuario, password);
            if (usuariodb == null) return Unauthorized();

            if (!ObtieneDatosCanal(chID)) return Unauthorized();

            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var playlistFile = $"{streamsFolder}{chID}_.m3u8";
            string playlistOutput = System.IO.File.ReadAllText(playlistFile);

            string pattern = @"(.*?).ts";
            Regex rg = new(pattern);
            var matches = rg.Matches(playlistOutput);
            /*
            int cont = 0;
            while (matches.Count < 3)
            {
                cont++;
                System.Threading.Thread.Sleep(700);

                playlistOutput = System.IO.File.ReadAllText(playlistFile);
                matches = rg.Matches(playlistOutput);
                if (cont >= 20)
                    return Unauthorized();
            }*/

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

            Helpers.Usuario.ActualizaInfoUsuario(usuariodb.UsuarioId, chID, userAgent, ip, usuariodb.ConexionesMaximas);

            return File(stream, "application/x-mpegurl", $"{chID}.m3u8");
        }

        [HttpGet("{usuario}/{password}/{token}/{segment}")]
        public IActionResult Hls(string usuario, string password, string token, string segment)
        {
            string tokenMatch = MD5($"{usuario}{password}zxcvbnm7852{segment}");
            if (token != tokenMatch) return Unauthorized();

            var streamsFolder = Constantes.Global.STREAMS_FOLDER;
            var segmentFile = $"{streamsFolder}{segment}";
            /*var fileBytes = System.IO.File.ReadAllBytes(segmentFile);
            MemoryStream stream = new(fileBytes);*/

            return PhysicalFile(segmentFile, "video/mp2t");

            //return File(stream, "video/mp2t", segment);
        }

        /*public Usuario VerificarUsuario(string usuario, string password)
        {
            usuario = usuario.Replace("UPDATE", "").Replace("INSERT", "").Replace("DELETE", "");
            password = password.Replace("UPDATE", "").Replace("INSERT", "").Replace("DELETE", "");
            //Verifica si usuario existe
            using (var connection = new MySqlConnection("server=127.0.0.1;Port=7999;uid=ticolineadb;pwd=Qawsedrf7852!;database=xtream_iptvpro;Allow User Variables=True;SSLMode=None"))
            {
                connection.Open();

                List<Usuario> usuarios = new();

                string query = "SELECT id,conexiones_maximas, habilitado FROM usuarios_ticolinea " +
                                                  "WHERE usuario = @usuario and clave = @clave;";

                using var cmd = new MySqlCommand(query, connection);
                cmd.Parameters.AddWithValue("@usuario", usuario);
                cmd.Parameters.AddWithValue("@clave", password);

                using (var reader = cmd.ExecuteReader())
                    while (reader.Read())
                    {
                        usuarios.Add(new Usuario
                        {
                            UsuarioId = reader.GetInt32(0),
                            ConexionesMaximas = reader.GetInt32(1),
                            Habilitado = reader.GetInt32(2),
                        });
                    }

                return usuarios.FirstOrDefault();
            }
        }*/

        private bool ObtieneDatosCanal(int chnId)
        {
            string ubicacionStreams = Constantes.Global.STREAMS_FOLDER;


            try
            {
                List<StreamDb> streams = new();

                using (Mariadb mariadb = new Mariadb(Constantes.Global.MARIADB_CONN))
                {
                    var cmd = mariadb.Conexion.CreateCommand();
                    cmd.CommandText = "SELECT fuente_stream,stream_id,probesize_ondemand,es_bajodemanda,proceso_id, transcode_audio, intervalo, segmentos, framerate, transcode, resolucion, bitrate FROM streams_tl a " +
                                    "INNER JOIN streams_info b " +
                                    "on a.id = b.stream_id " +
                                    $"WHERE iniciado = 1 AND stream_id = {chnId};";

                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
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
                                Bitrate = reader.GetString(11)
                            });
                        }
                    cmd.Connection?.Close();
                }


                var stream = streams.FirstOrDefault();

                if (stream == null)
                {
                    Console.WriteLine("Canal no encontrado");
                    return false;
                };


                if (stream.ProcesoId > -1)
                {
                    bool EstaCorriendoStream = Jobs.ObtenerProcesoFFMPEG(stream.ProcesoId);
                    if ((!EstaCorriendoStream && stream.EsBajoDemanda == 1) || (!EstaCorriendoStream && stream.EsBajoDemanda == 0))
                    {
                        Console.WriteLine("Canal sin proceso, iniciando stream");
                        //Inicia stream
                        Jobs.IniciarStream(stream);
                        System.Threading.Thread.Sleep(100);
                        bool archivoExiste = false;
                        int ciclo = 0;
                        while (archivoExiste == false && ciclo < 35)
                        {
                            archivoExiste = System.IO.File.Exists($"{ubicacionStreams}{stream.StreamId}_.m3u8");
                            ciclo++;
                            System.Threading.Thread.Sleep(400);

                            return false;
                        }

                        return true;
                    }
                    else return true;
                }
                else
                {
                    Console.WriteLine("Canal sin proceso, iniciado stream");

                    //Inicia stream
                    Jobs.IniciarStream(stream);
                    System.Threading.Thread.Sleep(100);
                    bool archivoExiste = false;
                    int ciclo = 0;
                    while (archivoExiste == false && ciclo < 35)
                    {
                        archivoExiste = System.IO.File.Exists($"{ubicacionStreams}{stream.StreamId}_.m3u8");
                        ciclo++;
                        System.Threading.Thread.Sleep(400);
                    }

                    return true;
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
    }
}
