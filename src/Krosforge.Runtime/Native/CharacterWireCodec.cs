using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;

namespace Krosforge.Runtime.Native;

internal sealed record CharacterDraftWire(string Name, int BreedId, int Gender, int HeadId, int? BodyId, ImmutableArray<int?> Colors);
internal sealed record EntityLookWire(int BoneId, ImmutableArray<int> Skins, ImmutableArray<int> Colors, ImmutableArray<int> Scales);
internal sealed record CharacterWire(long Id, string Name, int BreedId, int Gender, int HeadId, int BodyId, int Level, EntityLookWire Look);

/// <summary>Character contracts for the selected build, with tags resolved through the verified de-para.</summary>
internal sealed class CharacterWireCodec(int maxFields = 128)
{
    private static int F(string key) => Fields[key];
    private static int V(string key) => Fields[key];
    private static readonly Dictionary<string, int> Fields = new()
    {
        ["appearance.body"] = 7,
        ["appearance.breed"] = 4,
        ["appearance.color-channels"] = 6,
        ["appearance.colors"] = 8,
        ["appearance.gender"] = 3,
        ["appearance.head"] = 5,
        ["appearance.id"] = 1,
        ["appearance.name"] = 2,
        ["appearance.optional-variant"] = 6,
        ["creation.appearance"] = 1,
        ["creation.name-error"] = 3,
        ["creation.name-error-generic"] = 0,
        ["creation.result"] = 1,
        ["creation.result-failure"] = 1,
        ["creation.result-name-invalid"] = 2,
        ["creation.result-name-unavailable"] = 2,
        ["creation.result-slots-exhausted"] = 3,
        ["creation.result-success"] = 0,
        ["deletion.answer"] = 1,
        ["deletion.challenge"] = 3,
        ["deletion.confirm-id"] = 2,
        ["deletion.failure-result"] = 1,
        ["deletion.failure-value"] = 0,
        ["deletion.max-confirmation-bytes"] = 512,
        ["deletion.prepare-id"] = 1,
        ["deletion.question"] = 2,
        ["deletion.requires-secret"] = 1,
        ["deletion.token"] = 3,
        ["details.breed"] = 3,
        ["details.gender"] = 1,
        ["details.gender-value"] = 4,
        ["details.look"] = 6,
        ["full.details"] = 5,
        ["full.level"] = 3,
        ["full.name"] = 6,
        ["list.characters"] = 2,
        ["look.bone"] = 1,
        ["look.colors"] = 5,
        ["look.scales"] = 6,
        ["look.skins"] = 3,
        ["record.appearance"] = 5,
        ["record.full"] = 4,
        ["record.id"] = 3,
        ["selection.character"] = 1,
        ["selection.failure"] = 2,
        ["selection.id"] = 1,
        ["selection.success"] = 1,
        ["suggestion.name"] = 1,
    };

    public CharacterDraftWire ReadCreation(ReadOnlySpan<byte> bytes)
    {
        var request = ProtoObject.Read(bytes, maxFields); request.Only(F("creation.appearance"));
        var appearance = ProtoObject.Read(request.Bytes(F("creation.appearance")), maxFields);
        appearance.Only(F("appearance.id"), F("appearance.name"), F("appearance.gender"), F("appearance.breed"),
            F("appearance.head"), F("appearance.optional-variant"), F("appearance.body"), F("appearance.colors"));
        if (appearance.Has(F("appearance.id"))) appearance.UInt64(F("appearance.id")); // Never used as server identity.
        var variant = ProtoObject.Read(appearance.Bytes(F("appearance.optional-variant"), required: false), maxFields);
        variant.Only(); // This optional variant is not assigned by the recovered normal creation path.
        var name = appearance.Text(F("appearance.name"));
        var colors = PackedIntegers(appearance, F("appearance.colors"), V("appearance.color-channels")).Select(value =>
            value is uint.MaxValue or ulong.MaxValue ? (int?)null : value <= 0xffffff ? (int)value
                : throw new ProtocolException("Invalid RGB color encoding.")).ToImmutableArray();
        var body = Int32(appearance, "appearance.body", required: false);
        return new CharacterDraftWire(name, Int32(appearance, "appearance.breed", false), Int32(appearance, "appearance.gender", false),
            Int32(appearance, "appearance.head", false), body == 0 ? null : body, colors);
    }

    public long SelectionId(ReadOnlySpan<byte> payload) => Identifier(payload, "selection.id");
    public long DeletionPreparationId(ReadOnlySpan<byte> payload) => Identifier(payload, "deletion.prepare-id");
    public long DeletionConfirmationId(ReadOnlySpan<byte> payload)
    {
        var request = ProtoObject.Read(payload, maxFields);
        request.Only(F("deletion.confirm-id"), F("deletion.answer"), F("deletion.challenge"));
        foreach (var field in new[] { "deletion.answer", "deletion.challenge" })
            if (Encoding.UTF8.GetByteCount(request.Text(F(field), required: false)) > V("deletion.max-confirmation-bytes"))
                throw new ProtocolException("Deletion confirmation exceeds byte budget.");
        return PositiveId(request.UInt64(F("deletion.confirm-id")));
    }

    public byte[] List(IEnumerable<CharacterWire> characters)
    {
        var output = new ArrayBufferWriter<byte>();
        foreach (var character in characters) ProtoWriter.Bytes(output, F("list.characters"), Character(character));
        return output.WrittenSpan.ToArray();
    }
    public byte[] SelectionAccepted(CharacterWire character)
    {
        var success = new ArrayBufferWriter<byte>(); ProtoWriter.Bytes(success, F("selection.character"), Character(character));
        var result = new ArrayBufferWriter<byte>(); ProtoWriter.Bytes(result, F("selection.success"), success.WrittenSpan);
        return result.WrittenSpan.ToArray();
    }
    public byte[] SelectionRejected()
    {
        var output = new ArrayBufferWriter<byte>(); ProtoWriter.Bytes(output, F("selection.failure"), []);
        return output.WrittenSpan.ToArray();
    }
    public byte[] CreationResult(string semanticResult)
    {
        var output = new ArrayBufferWriter<byte>(); Number(output, "creation.result", V("creation.result-" + semanticResult));
        if (semanticResult is "name-invalid" or "name-unavailable") Number(output, "creation.name-error", V("creation.name-error-generic"));
        return output.WrittenSpan.ToArray();
    }
    public byte[] DeletionPrepared()
    {
        var output = new ArrayBufferWriter<byte>(); Number(output, "deletion.requires-secret", 0);
        Text(output, "deletion.question", ""); Text(output, "deletion.token", ""); return output.WrittenSpan.ToArray();
    }
    public byte[] DeletionRejected()
    {
        var output = new ArrayBufferWriter<byte>(); Number(output, "deletion.failure-result", V("deletion.failure-value"));
        return output.WrittenSpan.ToArray();
    }
    public byte[] SuggestedName(string name)
    { var output = new ArrayBufferWriter<byte>(); Text(output, "suggestion.name", name); return output.WrittenSpan.ToArray(); }

    public byte[] Character(CharacterWire character)
    {
        if (character.Id <= 0 || character.Level <= 0 || character.BreedId <= 0 || character.Gender is < 0 or > 1
            || character.HeadId < 0 || character.BodyId <= 0 || character.Look.Colors.Length != V("appearance.color-channels"))
            throw new ProtocolException("Invalid persisted character presentation.");
        var gender = new ArrayBufferWriter<byte>(); Number(gender, "details.gender-value", character.Gender);
        var details = new ArrayBufferWriter<byte>();
        // The details variant contains the gender descriptor at its own recovered field.
        Number(details, "details.breed", character.BreedId); ProtoWriter.Bytes(details, F("details.look"), Look(character.Look));
        ProtoWriter.Bytes(details, F("details.gender"), gender.WrittenSpan);
        var full = new ArrayBufferWriter<byte>(); Number(full, "full.level", character.Level); Text(full, "full.name", character.Name);
        ProtoWriter.Bytes(full, F("full.details"), details.WrittenSpan);
        var output = new ArrayBufferWriter<byte>(); Number(output, "record.id", character.Id);
        ProtoWriter.Bytes(output, F("record.full"), full.WrittenSpan);
        ProtoWriter.Bytes(output, F("record.appearance"), Appearance(character));
        return output.WrittenSpan.ToArray();
    }
    public byte[] Look(EntityLookWire look)
    {
        if (look.BoneId <= 0 || look.Skins.IsDefault || look.Scales.IsDefault || look.Colors.IsDefault
            || look.Colors.Any(color => color is < 0 or > 0xffffff)) throw new ProtocolException("Invalid entity look.");
        var output = new ArrayBufferWriter<byte>(); Number(output, "look.bone", look.BoneId);
        Packed(output, "look.skins", look.Skins.Select(value => (ulong)value));
        Packed(output, "look.colors", look.Colors.Select((value, index) => ((ulong)(index + 1) << 24) | (uint)value));
        Packed(output, "look.scales", look.Scales.Select(value => (ulong)value));
        return output.WrittenSpan.ToArray();
    }

    private byte[] Appearance(CharacterWire character)
    {
        var output = new ArrayBufferWriter<byte>(); Number(output, "appearance.id", character.Id); Text(output, "appearance.name", character.Name);
        Number(output, "appearance.gender", character.Gender); Number(output, "appearance.breed", character.BreedId);
        Number(output, "appearance.head", character.HeadId); Number(output, "appearance.body", character.BodyId);
        Packed(output, "appearance.colors", character.Look.Colors.Select(color => (ulong)color)); return output.WrittenSpan.ToArray();
    }
    private long Identifier(ReadOnlySpan<byte> payload, string field)
    {
        var request = ProtoObject.Read(payload, maxFields); request.Only(F(field)); return PositiveId(request.UInt64(F(field)));
    }
    private static long PositiveId(ulong id) => id is > 0 and <= long.MaxValue ? (long)id : throw new ProtocolException("Invalid character identity.");
    private int Int32(ProtoObject message, string key, bool required = true)
    {
        var value = message.UInt64(F(key), required);
        return value <= int.MaxValue ? (int)value : throw new ProtocolException("Integer outside nonnegative int32.");
    }
    private void Packed(IBufferWriter<byte> output, string key, IEnumerable<ulong> values)
    {
        var body = new ArrayBufferWriter<byte>(); foreach (var value in values) ProtoWriter.Varint(body, value);
        ProtoWriter.Bytes(output, F(key), body.WrittenSpan);
    }
    private void Number(IBufferWriter<byte> output, string key, long value)
    {
        if (value < 0) throw new ProtocolException("Unexpected negative character value.");
        ProtoWriter.UInt64(output, F(key), (ulong)value);
    }
    private void Text(IBufferWriter<byte> output, string key, string text) => ProtoWriter.Bytes(output, F(key), Encoding.UTF8.GetBytes(text));
    private static IEnumerable<ulong> PackedIntegers(ProtoObject message, int field, int maxCount)
    {
        var result = new List<ulong>();
        foreach (var value in message.Repeated(field))
        {
            if (value.WireType == 0) { Add(value.Number); continue; }
            if (value.WireType != 2) throw new ProtocolException("Invalid repeated integer wire type.");
            var offset = 0;
            while (offset < value.Bytes.Length)
            {
                ulong number = 0;
                for (var index = 0; ; index++)
                {
                    if (offset == value.Bytes.Length || index == 10) throw new ProtocolException("Truncated or oversized packed integer.");
                    var next = value.Bytes[offset++];
                    if (index == 9 && next > 1) throw new ProtocolException("Packed integer overflow.");
                    number |= (ulong)(next & 127) << (index * 7);
                    if ((next & 128) == 0) break;
                }
                Add(number);
            }
        }
        return result;
        void Add(ulong value)
        { if (result.Count >= maxCount) throw new ProtocolException("Repeated integer count exceeds budget."); result.Add(value); }
    }
}
