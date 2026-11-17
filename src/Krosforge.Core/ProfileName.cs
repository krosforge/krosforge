namespace Krosforge.Core;

public static class ProfileName
{
    public static string Normalize(string value)
    {
        value = value.Trim().Normalize();
        if (value.Length is < 1 or > 40 || value.Any(char.IsControl))
            throw new ArgumentException("Informe um apelido de 1 a 40 caracteres, sem caracteres de controle.");
        return value;
    }
    public static string Key(string value) => Normalize(value).ToUpperInvariant();
}
