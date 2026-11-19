using System.Collections.Immutable;
using System.Security.Cryptography;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed class CharacterService(CharacterStore store, CreationCatalog catalog, ExperienceCurve? experience = null)
{
    internal CharacterWire[] List(Guid profile) => store.List(profile, ServerPresentation.RealmId).Select(c =>
    {
        var a = c.Appearance;
        (int Bone, int[] Skins, int[] Scales) look;
        try { look = catalog.Look(a); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted appearance.", error); }
        var level = experience?.At(c.Experience).Level ?? (c.Experience == 0 ? 1 : throw new InvalidDataException("Experience curve missing."));
        return new CharacterWire(c.Id, c.Name, a.Breed, a.Gender, a.Head, a.Body, level,
            new EntityLookWire(look.Bone, look.Skins.ToImmutableArray(), a.Colors.ToImmutableArray(), look.Scales.ToImmutableArray()));
    }).ToArray();
    internal string Create(Guid profile, CharacterDraftWire draft)
        => Create(profile, draft, out _);
    internal string Create(Guid profile, CharacterDraftWire draft, out long createdId)
    {
        createdId = 0;
        string name;
        try { name = CharacterName.Normalize(draft.Name); }
        catch (ArgumentException) { return "name-invalid"; }
        CharacterAppearance appearance;
        try { appearance = catalog.Resolve(draft.BreedId, draft.Gender, draft.HeadId, draft.BodyId ?? 0, draft.Colors.ToArray()); }
        catch (ArgumentException) { return "failure"; }
        return store.Create(profile, ServerPresentation.RealmId, name, appearance, out createdId) switch
        {
            CharacterCreationResult.Success => "success", CharacterCreationResult.Full => "slots-exhausted",
            CharacterCreationResult.NameUnavailable => "name-unavailable", _ => "failure"
        };
    }
    internal bool Delete(Guid profile, long id) => store.Delete(profile, ServerPresentation.RealmId, id);
    internal string Suggest()
    {
        const string consonants = "bdfglmnrstv"; const string vowels = "aeiou";
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var letters = Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? consonants[RandomNumberGenerator.GetInt32(consonants.Length)] : vowels[RandomNumberGenerator.GetInt32(vowels.Length)]).ToArray();
            letters[0] = char.ToUpperInvariant(letters[0]); var name = new string(letters);
            if (store.Available(name, ServerPresentation.RealmId)) return name;
        }
        throw new IOException("Name suggestion unavailable.");
    }
}
