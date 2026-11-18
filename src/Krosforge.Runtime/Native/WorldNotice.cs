using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record WorldActor(CharacterWire Character, long Account, WorldLocation Location);
internal abstract record WorldNotice;
internal sealed record MapSnapshot(EntryMap Map, IReadOnlyList<WorldActor> Actors) : WorldNotice;
internal sealed record ActorAppeared(WorldActor Actor) : WorldNotice;
internal sealed record ActorDeparted(long Id) : WorldNotice;
internal sealed record ActorMoved(long Id, IReadOnlyList<int> Cells) : WorldNotice;
internal sealed record ActorCorrected(long Id, int Cell) : WorldNotice;
internal sealed record MovementReleased : WorldNotice;
internal sealed record MapLoading(long Map) : WorldNotice;
