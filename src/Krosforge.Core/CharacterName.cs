using System.Text;
namespace Krosforge.Core;

public static class CharacterName
{
    public static string Normalize(string input)
    {
        var value = input.Normalize(NormalizationForm.FormC);
        var letters = value.EnumerateRunes().ToArray();
        if (value.Length > 20 || letters.Length is < 3 or > 20 || value[0] == '-' || value[^1] == '-'
            || letters.Count(c => c.Value == '-') > 1 || letters.Any(c => c.Value != '-' && !Rune.IsLetter(c)))
            throw new ArgumentException("Invalid character name.");
        return value;
    }
    public static string Key(string name) => Normalize(name).ToUpperInvariant();
}
