using System.Text.RegularExpressions;

namespace ticolinea.stream.service.NodeConsole;

// Returns null when the input is acceptable, otherwise the message shown to the
// operator. Messages are Spanish because the console is.
public static class ConsoleValidation
{
    // Exactly the schemes FFmpeg is given as `-i` on this node. `file://` is
    // excluded on purpose: the service runs as its own user with read access to
    // the box, and a source field that accepts local paths turns the console
    // into an arbitrary-file-read primitive.
    private static readonly string[] AllowedSchemes =
        { "http://", "https://", "srt://", "rtmp://", "rtmps://", "rtsp://", "udp://" };

    private static readonly Regex UsernamePattern = new("^[a-z0-9._-]{3,32}$", RegexOptions.Compiled);

    public const int MaxChannelName = 120;
    public const int MaxCategoryName = 60;
    public const int MinPassword = 12;

    public static string? Channel(string? name, string? source)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "El nombre del canal es obligatorio.";
        if (n.Length > MaxChannelName) return $"El nombre del canal no puede superar {MaxChannelName} caracteres.";

        var s = (source ?? "").Trim();
        if (s.Length == 0) return "El origen del stream es obligatorio.";
        if (!AllowedSchemes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return "El origen debe iniciar con http, https, srt, rtmp, rtmps, rtsp o udp.";

        return null;
    }

    public static string? Category(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "El nombre de la categoría es obligatorio.";
        if (n.Length > MaxCategoryName) return $"El nombre de la categoría no puede superar {MaxCategoryName} caracteres.";
        return null;
    }

    public static string? NewUser(string? username, string? password)
    {
        if (!UsernamePattern.IsMatch(username ?? ""))
            return "El usuario debe tener entre 3 y 32 caracteres en minúscula (letras, números, punto, guion o guion bajo).";
        return Password(password);
    }

    public static string? Password(string? password)
    {
        if ((password ?? "").Length < MinPassword)
            return $"La contraseña debe tener al menos {MinPassword} caracteres.";
        return null;
    }
}
