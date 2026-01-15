using System.Text;

namespace ticolinea.stream.service.Helpers;

public class MD5
{
    public static string Encriptar(string s)
    {
        using var provider = System.Security.Cryptography.MD5.Create();
        StringBuilder builder = new();

        foreach (byte b in provider.ComputeHash(Encoding.UTF8.GetBytes(s)))
            builder.Append(b.ToString("x2").ToLower());

        return builder.ToString();
    }
}