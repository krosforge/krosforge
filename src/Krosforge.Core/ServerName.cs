namespace Krosforge.Core;

public static class ServerName
{
    public static string Normalize(string value)
    {
        if (value.Any(c => char.IsControl(c) || c is '<' or '>'))
            throw new ArgumentException("ErrorServerName");
        value = value.Trim().Normalize();
        if (value.Length is < 1 or > 40)
            throw new ArgumentException("ErrorServerName");
        return value;
    }
}
