using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class CharacterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "characters-" + Guid.NewGuid().ToString("N"));
    private static CharacterAppearance Appearance => new(1, ClientInstallation.SupportedBuild, 1, 0, 1, 1, [1, 2, 3, 4, 5, 6]);
    [Theory]
    [InlineData("Élodie")]
    [InlineData("Ana-Maria")]
    public void NamesNormalizeIndependentlyOfCulture(string name) => Assert.Equal(CharacterName.Key(name), CharacterName.Key(name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)));
    [Theory]
    [InlineData("Ab")]
    [InlineData("Abc1")]
    [InlineData("Ab c")]
    [InlineData("-Abc")]
    [InlineData("Abc-")]
    [InlineData("Ab-c-d")]
    [InlineData("Ab\0cd")]
    public void InvalidNamesAreRejected(string name) => Assert.Throws<ArgumentException>(() => CharacterName.Normalize(name));
    [Fact]
    public void OwnershipAndAppearanceSurviveRestartAndDeletionFreesName()
    {
        using var profiles = new ProfileStore(root); var a = profiles.Create("A"); var b = profiles.Create("B");
        var store = new CharacterStore(root);
        Assert.Equal(CharacterCreationResult.Success, store.Create(a.Id, 290, "Élodie", Appearance));
        Assert.Equal(CharacterCreationResult.NameUnavailable, store.Create(b.Id, 290, "e\u0301lodie", Appearance));
        var saved = Assert.Single(new CharacterStore(root).List(a.Id, 290));
        Assert.Equal(Appearance.Colors, saved.Appearance.Colors);
        Assert.Empty(store.List(b.Id, 290)); Assert.False(store.Delete(b.Id, 290, saved.Id)); Assert.False(store.Delete(a.Id, 291, saved.Id));
        Assert.True(store.Delete(a.Id, 290, saved.Id)); Assert.False(store.Delete(a.Id, 290, saved.Id));
        Assert.Equal(CharacterCreationResult.Success, store.Create(b.Id, 290, "Élodie", Appearance));
        Assert.True(Assert.Single(store.List(b.Id, 290)).Id > saved.Id);
    }
    [Fact]
    public async Task ConcurrentCreationsCannotExceedFiveSlots()
    {
        using var profiles = new ProfileStore(root); var profile = profiles.Create("A"); var store = new CharacterStore(root);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => store.Create(profile.Id, 290, "Hero" + (char)('a' + i), Appearance))));
        Assert.Equal(5, results.Count(r => r == CharacterCreationResult.Success));
        Assert.Equal(7, results.Count(r => r == CharacterCreationResult.Full)); Assert.Equal(5, store.Count(profile.Id, 290));
    }
    [Fact]
    public async Task ConcurrentProfilesCannotClaimTheSameName()
    {
        using var profiles = new ProfileStore(root); var a = profiles.Create("A"); var b = profiles.Create("B"); var store = new CharacterStore(root);
        var results = await Task.WhenAll(Task.Run(() => store.Create(a.Id, 290, "Alpha", Appearance)), Task.Run(() => store.Create(b.Id, 290, "ALPHA", Appearance)));
        Assert.Single(results, r => r == CharacterCreationResult.Success); Assert.Single(results, r => r == CharacterCreationResult.NameUnavailable);
    }
    [Fact]
    public void DecoderRejectsTruncatedOrOversizedBlocks()
    {
        Assert.Throws<InvalidDataException>(() => CreationDataReader.Lz4([0xf0], 16));
        Assert.Throws<InvalidDataException>(() => CreationDataReader.Lz4([0, 0, 0], 4));
        Assert.Throws<InvalidDataException>(() => CreationDataReader.Lz4([], 4194305));
        Assert.Equal(new byte[] { 65, 65, 65, 65, 65 }, CreationDataReader.Lz4([0x10, 65, 1, 0], 5));
    }
    [Fact]
    public void InstalledCatalogResolvesEveryClassAndGender()
    {
        var source = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(source is null, "Requires the separately installed reference client; no game assets in CI.");
        var catalog = new CreationCatalog(source!);
        Assert.Equal(19, catalog.BreedIds.Count());
        foreach (var breed in catalog.BreedIds) for (var gender = 0; gender <= 1; gender++)
        {
            var appearance = catalog.Resolve(breed, gender, 0, 0, []);
            Assert.Equal(6, appearance.Colors.Length); var look = catalog.Look(appearance);
            Assert.True(look.Bone > 0); Assert.NotEmpty(look.Skins); Assert.NotEmpty(look.Scales);
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
