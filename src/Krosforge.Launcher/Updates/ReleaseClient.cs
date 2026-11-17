using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Krosforge.Launcher.Updates;

internal sealed record AvailableUpdate(ReleaseVersion Version, string AssetName, Uri DownloadUrl, Uri ChecksumsUrl, long DownloadBytes);
internal sealed record DownloadProgress(long ReceivedBytes, long TotalBytes);
internal sealed record DownloadedUpdate(string Directory, string Executable, string ExecutableHash);
internal sealed class ReleaseClient(HttpClient http)
{
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    internal static readonly Uri LatestRelease = new("https://api.github.com/repos/krosforge/krosforge/releases/latest");
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
    public async Task<AvailableUpdate?> CheckAsync(ReleaseVersion current, CancellationToken token)
    {
        try { return await ReadReleaseAsync(current, token); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { throw new InvalidDataException("Invalid release metadata.", error); }
    }
    private async Task<AvailableUpdate?> ReadReleaseAsync(ReleaseVersion current, CancellationToken token)
    {
        using var response = await GetAsync(LatestRelease, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await ReadLimitedAsync(response, 2 * 1024 * 1024, token));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Missing release tag.");
        var version = ReleaseVersion.Parse(tag);
        if (version.Prerelease != null || version.CompareTo(current) <= 0) return null;
        if (tag != "v" + version && tag != version.ToString()) throw new InvalidDataException("Unsupported release tag.");
        var name = $"Krosforge-{version}-win-x64-portable.zip";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement Asset(string requested)
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == requested).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException("Missing or duplicate release asset.");
        }
        Uri DownloadUri(JsonElement asset)
        {
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            var expected = $"https://github.com/krosforge/krosforge/releases/download/{tag}/{asset.GetProperty("name").GetString()}";
            if (uri.AbsoluteUri != expected) throw new InvalidDataException("Unexpected release asset URL.");
            return uri;
        }
        var package = Asset(name);
        var bytes = package.GetProperty("size").GetInt64();
        if (bytes <= 0 || bytes > MaximumPackageBytes) throw new InvalidDataException("Invalid package size.");
        return new(version, name, DownloadUri(package), DownloadUri(Asset("SHA256SUMS")), bytes);
    }
    public async Task<DownloadedUpdate> DownloadAsync(AvailableUpdate update, string directory, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var package = Path.Combine(directory, "package.zip");
        var executable = Path.Combine(directory, "Krosforge.exe");
        try
        {
            using var checksums = await GetAsync(update.ChecksumsUrl, token);
            checksums.EnsureSuccessStatusCode();
            var hash = ReadChecksum(Encoding.UTF8.GetString(await ReadLimitedAsync(checksums, 1024 * 1024, token)), update.AssetName);
            using var response = await GetAsync(update.DownloadUrl, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.DownloadBytes)
                throw new InvalidDataException("Package length does not match release metadata.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > update.DownloadBytes || total > MaximumPackageBytes) throw new InvalidDataException("Package is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress?.Report(new(total, update.DownloadBytes));
                }
                if (total != update.DownloadBytes) throw new InvalidDataException("Incomplete package download.");
            }
            if (!string.Equals(await HashAsync(package, token), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package checksum does not match.");
            await ExtractExecutableAsync(package, executable, token);
            return new(directory, executable, await HashAsync(executable, token));
        }
        catch
        {
            if (File.Exists(package)) File.Delete(package);
            if (File.Exists(executable)) File.Delete(executable);
            throw;
        }
    }
    internal static string ReadChecksum(string text, string name)
    {
        var hashes = text.TrimStart('\uFEFF').Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 66 && (line[64..66] == "  " || line[64..66] == " *") && line[66..] == name)
            .Select(line => line[..64]).ToArray();
        if (hashes.Length != 1 || hashes[0].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Missing or invalid package checksum.");
        return hashes[0];
    }
    internal static async Task ExtractExecutableAsync(string package, string executable, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "Krosforge.exe") throw new InvalidDataException("Unexpected package contents.");
        var entry = zip.Entries[0];
        if (entry.Length <= 0 || entry.Length > MaximumPackageBytes || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new InvalidDataException("Invalid executable entry.");
        await using (var input = entry.Open())
        await using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            var buffer = new byte[128 * 1024]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                total += count;
                if (total > entry.Length || total > MaximumPackageBytes) throw new InvalidDataException("Executable exceeds the expected size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (total != entry.Length) throw new InvalidDataException("Executable is incomplete.");
        }
        try
        {
            using var stream = File.OpenRead(executable);
            using var reader = new PEReader(stream);
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64 || reader.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus)
                throw new InvalidDataException("The update is not a Windows x64 executable.");
        }
        catch (BadImageFormatException error) { throw new InvalidDataException("Invalid executable image.", error); }
    }
    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects < 6; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Untrusted update download URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Krosforge", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            uri = location is null ? throw new InvalidDataException("Invalid download redirect.") : new Uri(uri, location);
        }
        throw new InvalidDataException("Too many download redirects.");
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Update metadata is too large.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var target = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            if (target.Length + count > limit) throw new InvalidDataException("Update metadata is too large.");
            target.Write(buffer, 0, count);
        }
        return target.ToArray();
    }
}
