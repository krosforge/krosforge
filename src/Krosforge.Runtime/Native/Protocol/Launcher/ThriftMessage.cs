namespace Krosforge.Runtime.Native.Protocol.Launcher;

// Standard Apache Thrift TBinaryProtocol values, not Dofus message discriminators.
public enum ThriftMessageKind { Call = 1, Reply = 2, Exception = 3 }
public enum ThriftValueKind : byte { Bool = 2, Int32 = 8, String = 11, Struct = 12, List = 15 }
public sealed record ThriftValue(ThriftValueKind Kind, string? Text = null, int Number = 0,
    IReadOnlyDictionary<short, ThriftValue>? Members = null, IReadOnlyList<string>? Items = null)
{
    public static ThriftValue String(string text) => new(ThriftValueKind.String, text);
    public static ThriftValue Int32(int number) => new(ThriftValueKind.Int32, Number: number);
    public static ThriftValue Bool(bool value) => new(ThriftValueKind.Bool, Number: value ? 1 : 0);
    public static ThriftValue Struct(IReadOnlyDictionary<short, ThriftValue> members) => new(ThriftValueKind.Struct, Members: members);
    public static ThriftValue StringList(IReadOnlyList<string> items) => new(ThriftValueKind.List, Items: items);
}
public sealed record ThriftMessage(string Method, ThriftMessageKind Kind, int Sequence,
    IReadOnlyDictionary<short, ThriftValue> Fields)
{
    public string Text(short id) => Field(id, ThriftValueKind.String).Text!;
    public int Int32(short id) => Field(id, ThriftValueKind.Int32).Number;
    private ThriftValue Field(short id, ThriftValueKind kind) =>
        Fields.TryGetValue(id, out var value) && value.Kind == kind ? value
            : throw new ProtocolException("Missing or mistyped launcher argument.");
}
public sealed record ThriftLimits(int MaxMessageBytes, int MaxStringBytes, int MaxFields)
{
    public void Validate()
    {
        if (MaxMessageBytes <= 0 || MaxStringBytes <= 0 || MaxStringBytes > MaxMessageBytes || MaxFields <= 0)
            throw new ArgumentException("Invalid launcher codec limits.");
    }
}
