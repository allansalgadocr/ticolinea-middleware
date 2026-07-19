namespace ticolinea.stream.service.Helpers;

// Localización y lectura (tail) del log ACTUAL de log4net, para soporte remoto
// (GET /api/admin/logs). Debe reflejar exactamente log4net.config:
//   <file> = %property{LogDir}/TL.  +  datePattern yyyyMMdd'.log'  (staticLogFileName=false)
// → {LogDir}/TL.{yyyyMMdd}.log. El rolling es Composite (fecha + tamaño); los
// backups por tamaño llevan sufijo numérico (TL.20260719.log.1) — el archivo
// VIGENTE es siempre el sin numerar, que es el que se sirve.
// LogDir se resuelve igual que Log4netExtensions (que delega aquí): config
// Logging:Directory, con el path histórico del nodo main como default.
public static class LogTailHelper
{
    // Historical log path of the main node — same default as Log4netExtensions.
    public const string DefaultLogDirectory = "/home/ticolineaplay/logs";

    public const int DefaultLines = 200;
    public const int MaxLines = 1000;

    // Single source of truth for the log directory. Log4netExtensions calls this
    // before configuring the appender, and AdminController calls it when serving
    // the tail — both always agree on where the file lives.
    public static string ResolveLogDirectory(string? configuredDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory)
            ? DefaultLogDirectory
            : configuredDirectory.TrimEnd('/');

    public static string LogFileName(DateTime date) => $"TL.{date:yyyyMMdd}.log";

    public static string LogFilePath(string logDirectory, DateTime date) =>
        Path.Combine(ResolveLogDirectory(logDirectory), LogFileName(date));

    // lines query param → effective count: default 200, min 1, cap 1000.
    public static int ClampLineCount(int? requested)
    {
        var n = requested ?? DefaultLines;
        if (n < 1) return 1;
        return Math.Min(n, MaxLines);
    }

    // Last N lines WITHOUT loading the whole file into memory: stream line by
    // line keeping only a fixed-size tail buffer (a 50MB log day costs one pass,
    // never 50MB of allocations). FileShare.ReadWrite because the log4net
    // appender (MinimalLock) may hold the file open for writing while we read.
    public static List<string> TailLines(string filePath, int count)
    {
        var buffer = new Queue<string>(count);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (buffer.Count == count) buffer.Dequeue();
            buffer.Enqueue(line);
        }
        return buffer.ToList();
    }
}
