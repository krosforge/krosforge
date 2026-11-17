using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krosforge.Runtime;
namespace Krosforge.Launcher;

internal sealed class LanguagePreferences
{
    internal static readonly string[] SupportedLanguages = ["fr", "en", "de", "es", "pt"];
    private readonly string path;
    private readonly CultureInfo computerCulture;
    public LanguagePreferences(string dataDirectory, CultureInfo? computerCulture = null)
    {
        path = Path.Combine(Path.GetFullPath(dataDirectory), "launcher-preferences.json");
        this.computerCulture = computerCulture ?? CultureInfo.CurrentUICulture;
    }
    public string Load()
    {
        try
        {
            var preference = ReadDocument()["language"]?.GetValue<string>();
            return preference == "auto" || SupportedLanguages.Contains(preference) ? preference! : "auto";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return "auto"; }
    }
    public CultureInfo Resolve(string preference) => ResolveCulture(preference == "auto" ? computerCulture.Name : preference);
    public static CultureInfo ResolveCulture(string name)
    {
        try
        {
            var language = CultureInfo.GetCultureInfo(name).TwoLetterISOLanguageName;
            return CultureInfo.GetCultureInfo(SupportedLanguages.Contains(language) ? language : "en");
        }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en"); }
    }
    public void Save(string preference)
    {
        if (preference != "auto" && !SupportedLanguages.Contains(preference))
            throw new ArgumentException("Unsupported language preference.", nameof(preference));
        var directory = Path.GetDirectoryName(path)!;
        SafePaths.CheckTree(directory);
        Directory.CreateDirectory(directory);
        JsonObject document;
        try { document = ReadDocument(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { document = new JsonObject(); }
        document["language"] = preference;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private JsonObject ReadDocument() => File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("Expected a preferences object.")
        : new JsonObject();
}
