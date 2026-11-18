using System.Globalization;
namespace Krosforge.Runtime.Native;

internal sealed class CreationCatalog
{
    private readonly Dictionary<int, Dictionary<string, object>> breeds, heads, bodies;
    internal CreationCatalog(string root)
    {
        breeds = Read(root, "breeds", "f4b32b06cd33e1bdbce26794aabee34eea3f16066a98d8eb378685f73719993d");
        heads = Read(root, "heads", "4086568c107745d121c4e0f879b1219c2e40a496615de60c4e30fe4d67d66876");
        bodies = Read(root, "bodies", "b62822f9568fb730d91d3988e92e4d84eef1d3b3b0934ed434e1a67ae8c784e6");
    }
    internal CreationCatalog(Dictionary<int, Dictionary<string, object>> breeds,
        Dictionary<int, Dictionary<string, object>> heads, Dictionary<int, Dictionary<string, object>> bodies)
    { this.breeds = breeds; this.heads = heads; this.bodies = bodies; }
    private static Dictionary<int, Dictionary<string, object>> Read(string root, string name, string hash) =>
        CreationDataReader.Read(Path.Combine(root, "Dofus_Data", "StreamingAssets", "Content", "Data", $"data_assets_{name}dataroot.asset.bundle"), hash)
            .ToDictionary(x => Number(x, "id"));
    internal IEnumerable<int> BreedIds => breeds.Keys.Order();
    internal CharacterAppearance Resolve(int breed, int gender, int head, int body, int?[] colors)
    {
        if (!breeds.TryGetValue(breed, out var definition) || gender is < 0 or > 1 || colors.Length > 6 || colors.Any(c => c is < 0 or > 0xffffff)) throw new ArgumentException("Invalid appearance.");
        head = ResolveOption(heads, head, breed, gender); body = ResolveOption(bodies, body, breed, gender);
        var defaults = (Dictionary<string, object>)definition[gender == 0 ? "maleColors" : "femaleColors"];
        var values = (object[])defaults.Single().Value;
        if (values.Length != 6) throw new InvalidDataException("Unexpected color channels.");
        return new(1, ClientInstallation.SupportedBuild, breed, gender, head, body,
            values.Select((v, i) => i < colors.Length && colors[i] is { } c ? c : Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray());
    }
    internal (int Bone, int[] Skins, int[] Scales) Look(CharacterAppearance a)
    {
        var verified = Resolve(a.Breed, a.Gender, a.Head, a.Body, a.Colors.Select(c => (int?)c).ToArray());
        if (verified.Head != a.Head || verified.Body != a.Body) throw new InvalidDataException("Invalid persisted appearance.");
        var parts = ((string)breeds[a.Breed][a.Gender == 0 ? "maleLook" : "femaleLook"]).Trim('{', '}').Split('|');
        if (parts.Length < 4) throw new InvalidDataException("Invalid base look.");
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), Integers((string)bodies[a.Body]["skins"]).Concat(Integers((string)heads[a.Head]["skins"])).ToArray(), parts[3].Length == 0 ? [100] : Integers(parts[3]));
    }
    private static int[] Integers(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
    private static int Number(Dictionary<string, object> row, string key) => Convert.ToInt32(row[key], CultureInfo.InvariantCulture);
    private static int ResolveOption(Dictionary<int, Dictionary<string, object>> rows, int id, int breed, int gender)
    {
        bool Allowed(Dictionary<string, object> r) => Number(r, "breed") == breed && Number(r, "gender") == gender && Number(r, "availableAtCreation") == 1 && Number(r, "payable") == 0;
        if (id == 0) return rows.Values.Where(Allowed).OrderBy(r => Number(r, "order")).ThenBy(r => Number(r, "id")).Select(r => Number(r, "id")).First();
        if (!rows.TryGetValue(id, out var row) || !Allowed(row)) throw new ArgumentException("Unavailable appearance option.");
        return id;
    }
}
