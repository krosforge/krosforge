namespace Krosforge.Core;

public sealed record WorldLocation(long MapId, int CellId, int Direction, long Revision)
{
    public void Validate()
    {
        if (MapId <= 0 || CellId < 0 || Direction is < 0 or > 7 || Revision <= 0)
            throw new InvalidDataException("Invalid character location.");
    }
}
