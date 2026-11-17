using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Krosforge.Runtime;
namespace Krosforge.Launcher.Updates;

internal static class UpdatePaths
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint capacity);

    internal static string Normalize(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Validate the supplied path before normalization so aliases cannot hide a reparse point.
        SafePaths.CheckTree(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Update paths cannot be links.");
        if (!OperatingSystem.IsWindows()) return path;

        var suffix = new Stack<string>();
        var existing = path;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            suffix.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing) ?? throw new DirectoryNotFoundException(path);
        }
        var buffer = new StringBuilder(512);
        var length = GetLongPathNameW(existing, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetLongPathNameW(existing, buffer, (uint)buffer.Capacity);
        }
        if (length == 0) throw new IOException("Could not normalize an update path.", new Win32Exception(Marshal.GetLastWin32Error()));
        var normalized = buffer.ToString();
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) normalized = @"\\" + normalized[8..];
        else if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized[4..];
        while (suffix.TryPop(out var name)) normalized = Path.Combine(normalized, name);
        SafePaths.CheckTree(Directory.Exists(normalized) ? normalized : Path.GetDirectoryName(normalized)!);
        return Path.TrimEndingDirectorySeparator(normalized);
    }

    internal static bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
