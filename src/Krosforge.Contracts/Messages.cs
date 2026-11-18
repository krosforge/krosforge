using System.Text.Json;
namespace Krosforge.Contracts;

public sealed record Profile(Guid Id, string Name, long Slot);
public sealed record Installation(string Path, string Build);
public sealed record ServerSettings(string Name);
public sealed record Snapshot(Profile[] Profiles, Installation? Installation, bool HasActiveClients = false, ServerSettings? Server = null);
public sealed record LaunchStatus(Guid ProfileId, string Stage, long BytesCopied, long TotalBytes, string? Error = null, int? ProcessId = null);
public sealed record Request(int Version, string Operation, Guid? ProfileId = null, string? Value = null, string? Confirmation = null);
public sealed record Response(bool Success, string? Error, JsonElement? Data)
{
    public static Response Ok<T>(T value) => new(true, null, JsonSerializer.SerializeToElement(value));
    public static Response Fail(string error) => new(false, error, null);
}
public static class Wire
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 65536;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrameBytes)
            throw new InvalidDataException("ErrorFrameSize");
        var size = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var size = new byte[4];
        await stream.ReadExactlyAsync(size, token);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("ErrorInvalidMessage");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("ErrorEmptyMessage");
    }
}
public static class AppPaths
{
    public static string Data => ResolveData(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetCommandLineArgs().Contains("--installed"));
    public static string ResolveData(string applicationDirectory, string localApplicationData, bool installed = false)
    {
        applicationDirectory = Path.GetFullPath(applicationDirectory);
        return installed
            ? Path.Combine(localApplicationData, "KrosforgeClient")
            : Path.Combine(applicationDirectory, "Krosforge-data");
    }
    public static string Pipe(string directory) => "krosforge-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory))))[..24];
}
