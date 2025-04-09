using System.Linq;
using System.Collections;
using System.Xml.Serialization;
using ticolinea.stream.service.Db;
using MySqlConnector;

namespace ticolinea.stream.service.Helpers
{
    public static class xmlHelper
    {
        public static async Task Deserializar(string archivo, bool esLocal)
        {
            string xml = "";

            if (!esLocal)
            {
                HttpClient httpClient = new HttpClient();
                xml = httpClient.GetStringAsync($"https://ticolineaexterno.s3.amazonaws.com/ticolineatv/{archivo}").Result;
            }
            else
            {
                var xmlFile = $"{Constantes.Global.EPG_FOLDER}{archivo}";
                xml = System.IO.File.ReadAllText(xmlFile);
            }

            XmlSerializer serializer = new XmlSerializer(typeof(Tv));
            using (StringReader reader = new StringReader(xml))
            {
                var epg = (Tv)serializer.Deserialize(reader);
                List<string> canales = epg?.Programme.Select(s => s.Channel).Distinct().ToList();
                using (var cnn = new MySqlConnection(Constantes.Global.MARIADB_CONN))
                {
                    using (var cmd = cnn.CreateCommand())
                    {
                        if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                        if (canales.Any())
                        {
                            cmd.CommandText = "DELETE FROM epg_tl;";
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    foreach (string canal in canales)
                    {
                        var programaciones = epg.Programme.Where(x => x.Channel == canal);

                        foreach (var programacion in programaciones)
                        {
                            using (var cmdInsert = cnn.CreateCommand())
                            {
                                if(cnn.State==System.Data.ConnectionState.Closed) await cnn.OpenAsync();
                                cmdInsert.CommandText = "INSERT INTO `epg_tl` " +
                                          "(canal_epg,titulo,descripcion,inicio,fin,anno,icono,fecha_hora_inicio,fecha_hora_fin) " +
                                          "VALUES (@canal_epg,@titulo,@descripcion,@inicio,@fin,@anno,@icono,@fecha_hora_inicio,@fecha_hora_fin);";


                                long fechaHoraInicio = 0;
                                if (!string.IsNullOrWhiteSpace(programacion.Start))
                                {
                                    long.TryParse(programacion.Start.Substring(0, 12), out fechaHoraInicio);
                                }

                                long fechaHoraFin = 0;
                                if (!string.IsNullOrWhiteSpace(programacion.Stop))
                                {
                                    long.TryParse(programacion.Stop.Substring(0, 12), out fechaHoraFin);
                                }

                                cmdInsert.Parameters.AddWithValue("@canal_epg", programacion.Channel);
                                cmdInsert.Parameters.AddWithValue("@titulo", programacion.Title.Text?.Replace("\xCC\x81l", ""));
                                cmdInsert.Parameters.AddWithValue("@descripcion", programacion.Desc.Text);
                                cmdInsert.Parameters.AddWithValue("@inicio", programacion.Start);
                                cmdInsert.Parameters.AddWithValue("@fin", programacion.Stop);
                                cmdInsert.Parameters.AddWithValue("@anno", programacion.Date);
                                cmdInsert.Parameters.AddWithValue("@icono", programacion.Icon?.Src);
                                cmdInsert.Parameters.AddWithValue("@fecha_hora_inicio", fechaHoraInicio);
                                cmdInsert.Parameters.AddWithValue("@fecha_hora_fin", fechaHoraFin);

                               await cmdInsert.ExecuteNonQueryAsync();
                            }
                        }

                    }
                }
            }
        }
    }
}

[XmlRoot(ElementName = "icon")]
public class Icon
{

    [XmlAttribute(AttributeName = "src")]
    public string Src { get; set; } = "";
}

[XmlRoot(ElementName = "channel")]
public class Channel
{

    [XmlElement(ElementName = "display-name")]
    public string Displayname { get; set; } = "";

    [XmlElement(ElementName = "icon")]
    public Icon Icon { get; set; } = new Icon();

    [XmlAttribute(AttributeName = "id")]
    public string Id { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";
}

[XmlRoot(ElementName = "title")]
public class Title
{

    [XmlAttribute(AttributeName = "lang")]
    public string Lang { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";
}

[XmlRoot(ElementName = "desc")]
public class Desc
{

    [XmlAttribute(AttributeName = "lang")]
    public string Lang { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";
}

[XmlRoot(ElementName = "category")]
public class Category
{

    [XmlAttribute(AttributeName = "lang")]
    public string Lang { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";
}

[XmlRoot(ElementName = "programme")]
public class Programme
{

    [XmlElement(ElementName = "category")]
    public List<Category> Category { get; set; } = new List<Category>();

    [XmlElement(ElementName = "icon")]
    public Icon Icon { get; set; } = new Icon();

    [XmlElement(ElementName = "rating")]
    public Rating Rating { get; set; } = new Rating();

    [XmlAttribute(AttributeName = "start")]
    public string Start { get; set; } = "";

    [XmlAttribute(AttributeName = "stop")]
    public string Stop { get; set; } = "";

    [XmlAttribute(AttributeName = "channel")]
    public string Channel { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";

    [XmlElement(ElementName = "title")]
    public Title Title { get; set; } = new Title();

    [XmlElement(ElementName = "desc")]
    public Desc Desc { get; set; } = new Desc();

    [XmlElement(ElementName = "credits")]
    public Credits Credits { get; set; } = new Credits();

    [XmlElement(ElementName = "date")]
    public int Date { get; set; } = 0;

    [XmlElement(ElementName = "star-rating")]
    public Starrating Starrating { get; set; } = new Starrating();

    [XmlElement(ElementName = "sub-title")]
    public Subtitle Subtitle { get; set; } = new Subtitle();
}

[XmlRoot(ElementName = "credits")]
public class Credits
{

    [XmlElement(ElementName = "director")]
    public string Director { get; set; } = "";

    [XmlElement(ElementName = "actor")]
    public List<string> Actor { get; set; } = new List<string>();
}

[XmlRoot(ElementName = "rating")]
public class Rating
{

    [XmlElement(ElementName = "value")]
    public string Value { get; set; } = "";

    [XmlAttribute(AttributeName = "system")]
    public string System { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";
}

[XmlRoot(ElementName = "star-rating")]
public class Starrating
{

    [XmlElement(ElementName = "value")]
    public string Value { get; set; } = "";
}

[XmlRoot(ElementName = "sub-title")]
public class Subtitle
{

    [XmlAttribute(AttributeName = "lang")]
    public string Lang { get; set; } = "";

    [XmlText]
    public string Text { get; set; } = "";
}

[XmlRoot(ElementName = "tv")]
public class Tv
{

    [XmlElement(ElementName = "channel")]
    public List<Channel> Channel { get; set; } = new List<Channel>();

    [XmlElement(ElementName = "iepg")]
    public int Iepg { get; set; } = 0;

    [XmlElement(ElementName = "programme")]
    public List<Programme> Programme { get; set; } = new List<Programme>();
}

