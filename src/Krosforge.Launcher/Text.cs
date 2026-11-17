using System.Globalization;
using System.Resources;
namespace Krosforge.Launcher;

internal static class Text
{
    internal static readonly ResourceManager Resources = new("Krosforge.Launcher.Strings", typeof(Text).Assembly);
    public static CultureInfo Culture { get; private set; } = LanguagePreferences.ResolveCulture(CultureInfo.CurrentUICulture.Name);
    public static void Use(CultureInfo culture) => Culture = LanguagePreferences.ResolveCulture(culture.Name);
    public static string Get(string key) => Get(key, Culture);
    internal static string Get(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? throw new InvalidOperationException("Missing interface resource: " + key);
    public static string Error(string code) => Resources.GetString(code, Culture) ?? Get("ErrorUnexpected");
}
