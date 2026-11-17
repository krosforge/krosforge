using System.Text.RegularExpressions;
namespace Krosforge.Launcher.Updates;

internal sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<ReleaseVersion>
{
    public static ReleaseVersion Parse(string value)
    {
        var match = Regex.Match(value, @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            throw new InvalidDataException("Invalid release version.");
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (prerelease?.Split('.').Any(part => part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0') == true)
            throw new InvalidDataException("Invalid prerelease version.");
        return new(major, minor, patch, prerelease);
    }
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.'); var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumber = left[i].All(char.IsAsciiDigit); var rightNumber = right[i].All(char.IsAsciiDigit);
            result = leftNumber && rightNumber ? left[i].Length.CompareTo(right[i].Length) : leftNumber.CompareTo(rightNumber) * -1;
            if (result == 0) result = string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : "-" + Prerelease);
}
