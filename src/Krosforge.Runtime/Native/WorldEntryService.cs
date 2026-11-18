using System.Collections.Concurrent;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed class WorldEntryService(CharacterService characters, LocationStore locations, WorldEntryCatalog catalog)
{
    private readonly ConcurrentDictionary<long, Selection> active = new();
    private readonly WorldEntryCatalog catalog = catalog;
    internal sealed class Selection(WorldEntryService owner, object connection, CharacterWire character, long account) : IDisposable
    {
        internal object Connection { get; } = connection;
        internal CharacterWire Character { get; } = character;
        internal long Account { get; } = account;
        internal WorldLocation Location { get; set; } = null!;
        internal EntryAttributes Attributes => owner.catalog.Attributes;
        internal EntryMap Map => owner.catalog.Map(Location.MapId);
        internal bool ContextCreated { get; set; }
        internal bool MapReady { get; set; }
        public void Dispose() => owner.active.TryRemove(new KeyValuePair<long, Selection>(Character.Id, this));
    }
    internal Selection? Select(Guid profile, long account, object connection, long id)
    {
        var character = characters.List(profile).SingleOrDefault(c => c.Id == id);
        if (character is null) return null;
        var selection = new Selection(this, connection, character, account);
        if (!active.TryAdd(id, selection)) return null;
        try
        {
            var location = locations.LoadOrCreate(profile, ServerPresentation.RealmId, id, catalog.Initial, catalog.Validate);
            if (location is null) { selection.Dispose(); return null; }
            selection.Location = location;
            return selection;
        }
        catch { selection.Dispose(); throw; }
    }
}
