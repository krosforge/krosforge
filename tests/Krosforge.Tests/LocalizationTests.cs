using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Krosforge.Launcher;
using Xunit;
namespace Krosforge.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "language-test-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("fr-CA", "fr")]
    [InlineData("en-GB", "en")]
    [InlineData("de-AT", "de")]
    [InlineData("es-MX", "es")]
    [InlineData("pt-PT", "pt")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ja-JP", "en")]
    [InlineData("", "en")]
    public void AutomaticLanguageUsesSupportedParentOrEnglish(string computer, string expected)
    {
        var settings = new LanguagePreferences(directory, CultureInfo.GetCultureInfo(computer));
        Assert.Equal("auto", settings.Load());
        Assert.Equal(expected, settings.Resolve(settings.Load()).Name);
    }
    [Fact]
    public void ManualPreferenceSurvivesRestartAndAutomaticCanBeRestored()
    {
        var settings = new LanguagePreferences(directory, CultureInfo.GetCultureInfo("de-DE"));
        settings.Save("fr");
        var restarted = new LanguagePreferences(directory, CultureInfo.GetCultureInfo("pt-BR"));
        Assert.Equal("fr", restarted.Resolve(restarted.Load()).Name);
        restarted.Save("auto");
        var automatic = new LanguagePreferences(directory, CultureInfo.GetCultureInfo("es-ES"));
        Assert.Equal("auto", automatic.Load());
        Assert.Equal("es", automatic.Resolve(automatic.Load()).Name);
        Assert.Single(Directory.GetFiles(directory));
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"language\":\"it\"}")]
    [InlineData("{\"language\":42}")]
    [InlineData("{\"language\":\"en\",\"language\":\"fr\"}")]
    [InlineData("{\"language\":\"en\",\"other\":1,\"other\":2}")]
    public void DamagedOrUnsupportedPreferenceFallsBackToComputerLanguage(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "launcher-preferences.json"), json);
        var settings = new LanguagePreferences(directory, CultureInfo.GetCultureInfo("fr-FR"));
        Assert.Equal("auto", settings.Load());
        Assert.Equal("fr", settings.Resolve(settings.Load()).Name);
    }
    [Theory]
    [InlineData("{\"language\":\"en\",\"language\":\"fr\"}")]
    [InlineData("{\"language\":\"en\",\"other\":1,\"other\":2}")]
    public void SavingLanguageRecoversFromDuplicatePreferenceKeys(string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "launcher-preferences.json");
        File.WriteAllText(path, json);

        new LanguagePreferences(directory).Save("pt");

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Single(saved);
        Assert.Equal("pt", saved["language"]!.GetValue<string>());
        Assert.Equal("pt", new LanguagePreferences(directory).Load());
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact]
    public void SavingLanguagePreservesOtherPreferences()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "launcher-preferences.json");
        File.WriteAllText(path, "{\"language\":\"en\",\"other\":{\"number\":7}}");
        new LanguagePreferences(directory).Save("es");
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(7, saved["other"]!["number"]!.GetValue<int>());
        Assert.Equal("es", saved["language"]!.GetValue<string>());
    }
    [Fact]
    public void EveryLanguageContainsEveryResourceWithMatchingFormatArguments()
    {
        var neutral = Text.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var expected = neutral.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        foreach (var language in LanguagePreferences.SupportedLanguages)
        {
            var culture = language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language);
            var resources = Text.Resources.GetResourceSet(culture, true, false);
            Assert.NotNull(resources);
            var actual = resources.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var (key, value) in actual)
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.Equal(Arguments(expected[key]), Arguments(value));
                Assert.Equal(value, Text.Get(key, CultureInfo.GetCultureInfo(language)));
            }
        }
    }
    private static string[] Arguments(string value) => Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order().ToArray();
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
