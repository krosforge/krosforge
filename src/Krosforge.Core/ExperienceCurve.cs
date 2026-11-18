namespace Krosforge.Core;

public sealed record ExperienceBand(int Level, long Floor, long Ceiling);
public sealed class ExperienceCurve
{
    private readonly long[] floors;
    public ExperienceCurve(IEnumerable<long> floors)
    {
        this.floors = floors.ToArray();
        if (this.floors.Length < 2 || this.floors[0] != 0 ||
            this.floors.Zip(this.floors.Skip(1)).Any(pair => pair.Second <= pair.First))
            throw new ArgumentException("Experience thresholds must start at zero and increase strictly.");
    }
    public ExperienceBand At(long experience)
    {
        if (experience < 0) throw new ArgumentOutOfRangeException(nameof(experience));
        var found = Array.BinarySearch(floors, experience);
        var index = found >= 0 ? found : ~found - 1;
        return new(index + 1, floors[index], index + 1 < floors.Length ? floors[index + 1] : floors[index]);
    }
}
