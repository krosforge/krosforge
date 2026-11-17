using Krosforge.Contracts;
namespace Krosforge.Runtime;

public static class ClientInstallation
{
    public const string SupportedBuild = "3.7.14.18";
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ankama", "Dofus-dofus3");
    public static Installation Inspect(string path)
    {
        path = Path.GetFullPath(path);
        SafePaths.CheckTree(path);
        var version = Path.Combine(path, "Dofus_Data", "StreamingAssets", "version");
        if (!File.Exists(Path.Combine(path, "Dofus.exe")) || !File.Exists(Path.Combine(path, "GameAssembly.dll")) || !File.Exists(version))
            throw new ArgumentException("ErrorCompleteInstallation");
        var lines = File.ReadAllLines(version).Where(x => x.StartsWith("Version=", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 1 || lines[0] != "Version=" + SupportedBuild)
            throw new ArgumentException("ErrorClientBuild");
        ClientCompatibility.Validate(path);
        return new(path, SupportedBuild);
    }
    public static Installation? Discover(string? configured)
    {
        foreach (var path in new[] { configured, DefaultPath }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
            try
            {
                return Inspect(path!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
public static class SafePaths
{
    public static void CheckTree(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item != null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ErrorUnsafePath");
    }
    public static void DeleteOwnedProfile(string root, Guid id)
    {
        var target = Path.Combine(root, "clients", id.ToString("N"));
        DeleteOwnedDirectory(target);
        DeleteOwnedDirectory(Path.Combine(root, "logs", id.ToString("N")));
    }
    internal static void DeleteOwnedDirectory(string target)
    {
        CheckTree(target);
        if (!Directory.Exists(target))
            return;
        var stack = new Stack<string>();
        stack.Push(target);
        while (stack.TryPop(out var directory))
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ErrorUnsafeCopy");
            if ((attributes & FileAttributes.Directory) != 0)
                stack.Push(entry);
        }
        Directory.Delete(target, true);
    }
}
