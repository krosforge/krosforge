using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace Krosforge.Runtime.Native;

// Restricted reader for hash-pinned UnityFS 8 / serialized v22 catalogs and map geometry.
// Reads embedded schemas; never loads client code or extracts files to disk.
internal static class CreationDataReader
{
    private sealed class Reader(byte[] data, int position = 0)
    {
        internal int Position = position;
        internal byte[] Take(int n)
        {
            if (n < 0 || n > data.Length - Position) throw new InvalidDataException("Invalid catalog extent.");
            var value = data.AsSpan(Position, n).ToArray(); Position += n; return value;
        }
        internal byte U8() => Take(1)[0];
        internal short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        internal int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        internal uint Big32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ulong Big64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal ushort Big16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal void Align(int n = 4) => Take((n - Position % n) % n);
        internal string CString() { var bytes = new List<byte>(); for (var i = 0; i < 65536; i++) { var b = U8(); if (b == 0) return Utf8.GetString(bytes.ToArray()); bytes.Add(b); } throw new InvalidDataException("Catalog string too long."); }
        internal string Text() { var text = Utf8.GetString(Take(Count())); Align(); return text; }
        internal int Count() { var n = I32(); if (n is < 0 or > 100000) throw new InvalidDataException("Catalog count rejected."); return n; }
    }
    private sealed record Node(int Level, int Flags, string Type, string Name, int Size, int Meta);
    private sealed record Type(int Class, Node[] Nodes);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static Dictionary<string, object>[] Read(string path, string hash, bool includeKeys = false, int maxOutput = 4194304, IReadOnlySet<long>? selectedKeys = null, int maxInput = 1048576,
        bool resolveReferences = false)
    {
        if (maxInput is < 1 or > 4194304) throw new ArgumentOutOfRangeException(nameof(maxInput));
        SafePaths.CheckTree(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > maxInput) throw new InvalidDataException("Invalid catalog file.");
        var raw = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(raw)) != hash) throw new InvalidDataException("Unsupported client catalog.");
        if (maxOutput is < 1 or > 67108864) throw new ArgumentOutOfRangeException(nameof(maxOutput));
        return Serialized(Unpack(raw, maxOutput), includeKeys: includeKeys, selectedKeys: selectedKeys, resolveReferences: resolveReferences);
    }
    internal static Dictionary<string, object>[] ReadMaps(string path, string hash, params int[] mapIds)
    {
        SafePaths.CheckTree(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16777216)
            throw new InvalidDataException("Invalid map bundle.");
        var raw = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(raw)) != hash) throw new InvalidDataException("Unsupported map bundle.");
        return Serialized(Unpack(raw, 67108864), maps: mapIds.ToHashSet());
    }
    internal static byte[] Unpack(byte[] raw, int maxOutput = 4194304)
    {
        var r = new Reader(raw);
        if (r.CString() != "UnityFS" || r.Big32() != 8) throw new InvalidDataException("Unsupported bundle.");
        r.CString(); r.CString(); var total = r.Big64(); var packed = checked((int)r.Big32()); var size = checked((int)r.Big32()); var flags = r.Big32();
        if (total != (ulong)raw.Length || flags != 0x243) throw new InvalidDataException("Unsupported bundle flags.");
        r.Align(16); var info = new Reader(Lz4(r.Take(packed), size)); info.Take(16);
        var count = checked((int)info.Big32()); if (count < 1 || count > (maxOutput == 4194304 ? 128 : 4096)) throw new InvalidDataException("Invalid block count.");
        var blocks = new List<(int Size, int Packed, int Mode)>();
        for (var i = 0; i < count; i++) blocks.Add((checked((int)info.Big32()), checked((int)info.Big32()), info.Big16() & 63));
        if (info.Big32() != 1) throw new InvalidDataException("Expected one catalog node.");
        var offset = checked((int)info.Big64()); var length = checked((int)info.Big64()); info.Big32(); info.CString();
        if (info.Position != size || blocks.Sum(b => (long)b.Size) > maxOutput) throw new InvalidDataException("Invalid catalog index.");
        r.Align(16); using var output = new MemoryStream();
        foreach (var block in blocks)
        {
            var bytes = r.Take(block.Packed);
            if (block.Mode is 2 or 3) bytes = Lz4(bytes, block.Size);
            else if (block.Mode == 1)
            {
                if (bytes.Length < 5 || block.Size < 0 || block.Size > maxOutput || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1, 4)) > 16777216) throw new InvalidDataException("Invalid LZMA properties.");
                using var input = new MemoryStream(bytes, 5, bytes.Length - 5, false);
                using var decoder = SharpCompress.Compressors.LZMA.LzmaStream.Create(bytes[..5], input, bytes.Length - 5, block.Size);
                bytes = new byte[block.Size]; decoder.ReadExactly(bytes);
                if (decoder.ReadByte() != -1) throw new InvalidDataException("LZMA size mismatch.");
            }
            else if (block.Mode != 0 || bytes.Length != block.Size) throw new InvalidDataException("Unsupported block.");
            output.Write(bytes);
        }
        if (r.Position != raw.Length || offset < 0 || length < 0 || offset + (long)length > output.Length) throw new InvalidDataException("Invalid node extent.");
        return output.ToArray().AsSpan(offset, length).ToArray();
    }
    internal static byte[] Lz4(byte[] input, int size)
    {
        if (size is < 0 or > 4194304) throw new InvalidDataException("LZ4 size rejected.");
        var output = new byte[size]; var src = 0; var dst = 0;
        int Next() => src < input.Length ? input[src++] : throw new InvalidDataException("Truncated LZ4.");
        int Length(int initial) { var n = initial; if (n == 15) { int b; do { b = Next(); n = checked(n + b); } while (b == 255); } return n; }
        while (src < input.Length)
        {
            var token = Next(); var literal = Length(token >> 4);
            if (literal > input.Length - src || literal > size - dst) throw new InvalidDataException("Invalid LZ4 literal.");
            input.AsSpan(src, literal).CopyTo(output.AsSpan(dst)); src += literal; dst += literal;
            if (src == input.Length) break;
            var offset = Next() | Next() << 8; var match = checked(Length(token & 15) + 4);
            if (offset == 0 || offset > dst || match > size - dst) throw new InvalidDataException("Invalid LZ4 match.");
            for (var i = 0; i < match; i++) { output[dst] = output[dst - offset]; dst++; }
        }
        if (dst != size) throw new InvalidDataException("LZ4 size mismatch."); return output;
    }
    private static Dictionary<string, object>[] Serialized(byte[] bytes, IReadOnlySet<int>? maps = null, bool includeKeys = false, IReadOnlySet<long>? selectedKeys = null,
        bool resolveReferences = false)
    {
        var h = new Reader(bytes); h.Big32(); h.Big32();
        if (h.Big32() != 22) throw new InvalidDataException("Unsupported serialized file.");
        h.Big32(); if (h.U8() != 0) throw new InvalidDataException("Unsupported byte order."); h.Take(3);
        var metadata = checked((int)h.Big32()); var total = h.Big64(); var start = checked((int)h.Big64()); h.Big64();
        if (total != (ulong)bytes.Length || metadata > start - 48 || start > bytes.Length) throw new InvalidDataException("Invalid metadata.");
        var r = new Reader(bytes, 48); r.CString(); r.I32(); if (r.U8() != 1) throw new InvalidDataException("Missing schemas.");
        Node[] Tree()
        {
            var count = r.Count(); var strings = r.Count();
            var nodes = new List<(int Level, int Flags, uint Type, uint Name, int Size, int Meta)>();
            for (var i = 0; i < count; i++) { r.I16(); var level = r.U8(); var flags = r.U8(); var type = (uint)r.I32(); var name = (uint)r.I32(); var size = r.I32(); r.I32(); var meta = r.I32(); r.I64(); nodes.Add((level, flags, type, name, size, meta)); }
            var text = r.Take(strings);
            string Resolve(uint p) => (p & 0x80000000) != 0 ? "common:" + (p & 0x7fffffff) : new Reader(text, checked((int)p)).CString();
            return nodes.Select(n => new Node(n.Level, n.Flags, Resolve(n.Type), Resolve(n.Name), n.Size, n.Meta)).ToArray();
        }
        Type ReadType(bool reference)
        {
            var cid = r.I32(); r.U8(); var index = r.I16(); if (cid == 114 || reference && index >= 0) r.Take(16); r.Take(16);
            return new(cid, Tree());
        }
        var types = new List<Type>(); var count = r.Count();
        for (var i = 0; i < count; i++) { types.Add(ReadType(false)); var deps = r.Count(); r.Take(checked(deps * 4)); }
        var objects = new List<(int Offset, int Length, int Type)>(); count = r.Count();
        for (var i = 0; i < count; i++) { r.Align(); r.I64(); var offset = checked(start + (int)r.I64()); var len = r.I32(); var type = r.I32(); if (offset < start || len < 0 || len > bytes.Length - offset || type < 0 || type >= types.Count) throw new InvalidDataException("Invalid object."); objects.Add((offset, len, type)); }
        count = r.Count(); for (var i = 0; i < count; i++) { r.I32(); r.Align(); r.I64(); }
        count = r.Count(); for (var i = 0; i < count; i++) { r.CString(); r.Take(16); r.I32(); r.CString(); }
        var schemas = new Dictionary<(string, string, string), Node[]>(); count = r.Count();
        for (var i = 0; i < count; i++) { var t = ReadType(true); schemas.Add((r.CString(), r.CString(), r.CString()), t.Nodes); }
        r.CString(); if (r.Position != 48 + metadata) throw new InvalidDataException("Unconsumed metadata.");
        object Value(Node[] nodes, int index, int depth = 0)
        {
            if (depth > 32) throw new InvalidDataException("Schema nesting rejected.");
            var n = nodes[index]; var children = new List<int>();
            for (var i = index + 1; i < nodes.Length && nodes[i].Level > n.Level; i++) if (nodes[i].Level == n.Level + 1) children.Add(i);
            object value;
            switch (n.Type)
            {
                case "common:840": case "string": value = r.Text(); break;
                case "SInt8": case "common:821": value = unchecked((sbyte)r.U8()); break;
                case "bool": case "UInt8": case "common:928": value = r.U8(); break;
                case "int": case "SInt32": case "common:222": case "UInt32": case "common:934": value = r.I32(); break;
                case "SInt64": case "common:814": case "UInt64": value = r.I64(); break;
                case "SInt16": case "common:800": case "UInt16": case "common:907": value = r.I16(); break;
                case "float": case "common:161": value = BitConverter.Int32BitsToSingle(r.I32()); break;
                case "double": case "common:117": case "common:140": value = BitConverter.Int64BitsToDouble(r.I64()); break;
                default:
                    if ((n.Flags & 1) != 0) { if (children.Count != 2) throw new InvalidDataException("Invalid array schema."); var length = r.Count(); value = Enumerable.Range(0, length).Select(_ => Value(nodes, children[1], depth + 1)).ToArray(); }
                    else if (children.Count > 0) value = children.ToDictionary(i => nodes[i].Name, i => Value(nodes, i, depth + 1));
                    else if (n.Size == 0) value = new Dictionary<string, object>();
                    else throw new InvalidDataException("Unsupported catalog schema."); break;
            }
            if ((n.Meta & 0x4000) != 0) r.Align(); return value;
        }
        if (maps is not null)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var item in objects.Where(o => types[o.Type].Class == 114 && types[o.Type].Nodes.Any(n => n.Name == "cellsData")))
            {
                var nodes = types[item.Type].Nodes;
                r = new Reader(bytes.AsSpan(item.Offset, item.Length).ToArray());
                r.I32(); r.I64(); r.U8(); r.Align(); r.I32(); r.I64(); r.Text();
                var payloadStart = r.Position;
                if (!maps.Contains(r.I32())) continue;
                r.Position = payloadStart;
                var row = new Dictionary<string, object>();
                for (var j = 12; j < nodes.Length; j++)
                {
                    if (nodes[j].Level != 1) continue;
                    row.Add(nodes[j].Name, Value(nodes, j));
                    if (nodes[j].Name == "rightArrowCellList") break;
                }
                if (!row.ContainsKey("rightArrowCellList")) throw new InvalidDataException("Incomplete map schema.");
                result.Add(row);
            }
            if (result.Count != maps.Count) throw new InvalidDataException("No map geometry found.");
            return result.ToArray();
        }
        var obj = objects.Single(o => types[o.Type].Class == 114); r = new Reader(bytes.AsSpan(obj.Offset, obj.Length).ToArray());
        r.I32(); r.I64(); r.U8(); r.Align(); r.I32(); r.I64(); r.Text();
        count = r.Count(); var keys = Enumerable.Range(0, count).Select(_ => r.I64()).ToArray(); count = r.Count(); var ids = Enumerable.Range(0, count).Select(_ => r.I64()).ToArray();
        if (keys.Length != ids.Length || keys.Distinct().Count() != keys.Length) throw new InvalidDataException("Invalid dictionary.");
        if (selectedKeys is not null && !selectedKeys.All(key => keys.Contains(key))) throw new InvalidDataException("Missing catalog key.");
        var selected = Enumerable.Range(0, keys.Length).Where(i => selectedKeys is null || selectedKeys.Contains(keys[i])).ToArray();
        var wanted = selected.Select(i => ids[i]).ToHashSet();
        r.Align(); if (r.I32() != 2) throw new InvalidDataException("Unsupported references.");
        var refs = new Dictionary<long, Dictionary<string, object>>(); count = r.Count();
        var seen = new HashSet<long>();
        for (var i = 0; i < count; i++)
        {
            var id = r.I64(); var nodes = schemas[(r.Text(), r.Text(), r.Text())]; var row = new Dictionary<string, object>();
            if (!seen.Add(id)) throw new InvalidDataException("Duplicate catalog reference.");
            var keep = resolveReferences || wanted.Contains(id);
            for (var j = 0; j < nodes.Length; j++) if (nodes[j].Level == 1 && nodes[j].Type != "ManagedReferencesRegistry")
            {
                var value = Value(nodes, j);
                if (keep) row.Add(nodes[j].Name, value);
            }
            if (keep) refs.Add(id, row);
        }
        if (r.Position != obj.Length) throw new InvalidDataException("Unconsumed catalog data.");
        if (ids.Any(id => !seen.Contains(id))) throw new InvalidDataException("Unresolved catalog reference.");
        if (includeKeys) foreach (var i in selected) refs[ids[i]].Add("$key", keys[i]);
        object Resolve(object value, HashSet<long> path, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Catalog reference nesting rejected.");
            if (value is object[] array) return array.Select(item => Resolve(item, path, depth + 1)).ToArray();
            if (value is not Dictionary<string, object> fields) return value;
            if (fields.Count == 1 && fields.TryGetValue("rid", out var identity))
            {
                var id = (long)identity;
                if (id == -2) return null!; // Unity's explicit null managed-reference sentinel.
                if (!refs.TryGetValue(id, out var target) || !path.Add(id)) throw new InvalidDataException("Unresolved or cyclic catalog reference.");
                var resolved = Resolve(target, path, depth + 1); path.Remove(id); return resolved;
            }
            return fields.ToDictionary(pair => pair.Key, pair => Resolve(pair.Value, path, depth + 1));
        }
        return selected.Select(i => resolveReferences ? (Dictionary<string, object>)Resolve(refs[ids[i]], [ids[i]], 0) : refs[ids[i]]).ToArray();
    }
}
