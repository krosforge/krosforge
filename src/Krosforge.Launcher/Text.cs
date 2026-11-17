using System.Globalization;
using System.Resources;
namespace Krosforge.Launcher;

internal static class Text
{
    private static readonly ResourceManager Resources = new("Krosforge.Launcher.Strings", typeof(Text).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? throw new InvalidOperationException("Missing interface resource: " + key);
}
