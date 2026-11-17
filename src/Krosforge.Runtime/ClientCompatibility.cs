using System.Security.Cryptography;
namespace Krosforge.Runtime;

public static class ClientCompatibility
{
    public static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["Dofus.exe"] = "fc340185b49f8dfa4c1b6a13c81e3511d38758eb079771595e577a8eb90d0279",
        ["GameAssembly.dll"] = "af866f65f0d08eebe9008ffda54462e2814d994c81059fffd766f0d685503ab3",
        ["Dofus_Data/globalgamemanagers"] = "cd051429f393988171d44cf8124cc6c4c1267480be7875d480e7495b35ba6d9f",
        ["Dofus_Data/il2cpp_data/Metadata/global-metadata.dat"] = "781890cf890114c5231b5bf04595acbceaf8317f7998fd8053c017de1ae23206"
    };
    public static void Validate(string root)
    {
        foreach (var (name, hash) in Hashes)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            SafePaths.CheckTree(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ErrorInstallationLinks");
            using var input = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != hash)
                throw new IOException("ErrorClientFiles");
        }
    }
}
