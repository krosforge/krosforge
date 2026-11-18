using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class ServerSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "server-settings-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void RewardRatesPersistTogetherAndLegacyRenamePreservesThem()
    {
        using (var runtime = new RuntimeService(root))
        {
            Assert.Equal(new ServerSettings("Krosforge"), Snapshot(runtime).Server);
            var settings = new ServerSettings("Local", 2.5m, 3m);
            Assert.True(runtime.Handle(new(Wire.Version, "configure-server-settings", Value: JsonSerializer.Serialize(settings))).Success);
            Assert.True(runtime.Handle(new(Wire.Version, "configure-server", Value: "Renamed")).Success);
        }
        using var restarted = new RuntimeService(root);
        Assert.Equal(new ServerSettings("Renamed", 2.5m, 3m), Snapshot(restarted).Server);
        foreach (var invalid in new[] { new ServerSettings("Invalid", -1m, 1m), new("Invalid", 1m, 1001m) })
            Assert.False(restarted.Handle(new(Wire.Version, "configure-server-settings", Value: JsonSerializer.Serialize(invalid))).Success);
        Assert.False(restarted.Handle(new(Wire.Version, "configure-server-settings", Value: "malformed")).Success);
        Assert.Equal(new ServerSettings("Renamed", 2.5m, 3m), Snapshot(restarted).Server);
    }
    [Fact]
    public void DefaultAndRenameSurviveRestartWithoutChangingProfiles()
    {
        Guid id;
        using (var runtime = new RuntimeService(root))
        {
            Assert.Equal("Krosforge", Snapshot(runtime).Server!.Name);
            id = runtime.Handle(new(Wire.Version, "create", Value: "Profile")).Data!.Value.Deserialize<Profile>()!.Id;
            Assert.True(runtime.Handle(new(Wire.Version, "configure-server", Value: "  São João  ")).Success);
            Assert.False(runtime.Handle(new(Wire.Version, "configure-server", Value: "<b>bad</b>")).Success);
        }
        using var reopened = new RuntimeService(root);
        var snapshot = Snapshot(reopened);
        Assert.Equal("São João", snapshot.Server!.Name);
        Assert.Equal(id, Assert.Single(snapshot.Profiles).Id);
    }
    [Fact]
    public void RuntimeRejectsConfigurationDuringPreparation()
    {
        using var runtime = new RuntimeService(root);
        var manager = (ClientLaunchManager)typeof(RuntimeService).GetField("launches", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!;
        var jobs = (System.Collections.Concurrent.ConcurrentDictionary<Guid, ClientLaunchManager.LaunchJob>)typeof(ClientLaunchManager).GetField("jobs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        var id = Guid.NewGuid();
        jobs[id] = new ClientLaunchManager.LaunchJob(id);
        Assert.True(runtime.HasActiveClients);
        var response = runtime.Handle(new(Wire.Version, "configure-server", Value: "Changed"));
        Assert.False(response.Success);
        Assert.Equal("ErrorCloseGameFirst", response.Error);
        Assert.Equal("ErrorCloseGameFirst", runtime.Handle(new(Wire.Version, "configure-server-settings", Value: "{}")).Error);
        Assert.Equal("Krosforge", Snapshot(runtime).Server!.Name);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("<size=80>name")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void InvalidNamesAreRejected(string name) => Assert.Throws<ArgumentException>(() => ServerName.Normalize(name));
    [Fact]
    public void RenameOnlyChangesTheSelectedIndexAndAppendsTheName()
    {
        var original = Fixture();
        var changed = ServerPresentation.Rename(original, "São João");
        Assert.Equal("São João", Lookup(changed, ServerPresentation.NameKey));
        Assert.Equal("Other", Lookup(changed, 7));
        Assert.Equal("Original", Lookup(original, ServerPresentation.NameKey));
        var expected = (byte[])changed[..original.Length].Clone();
        original.AsSpan(11, 4).CopyTo(expected.AsSpan(11));
        Assert.Equal(original, expected);
    }
    [Fact]
    public void InvalidOrDuplicateIndexIsRejected()
    {
        var missing = Fixture(); BitConverter.GetBytes(8).CopyTo(missing, 7);
        Assert.Throws<IOException>(() => ServerPresentation.Rename(missing, "New"));
        var duplicate = Fixture(); BitConverter.GetBytes(ServerPresentation.NameKey).CopyTo(duplicate, 15);
        Assert.Throws<IOException>(() => ServerPresentation.Rename(duplicate, "New"));
        var invalid = Fixture(); BitConverter.GetBytes(int.MaxValue).CopyTo(invalid, 11);
        Assert.Throws<IOException>(() => ServerPresentation.Rename(invalid, "New"));
        Assert.Throws<IOException>(() => ServerPresentation.Rename([2], "New"));
    }
    [Fact]
    public async Task InterruptedPromotionCanBeRetriedWithoutGrowthOrSourceChanges()
    {
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
        var bytes = Fixture(); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var hashes = new Dictionary<string,string> { ["en"] = hash, ["pt"] = hash };
        foreach (var language in hashes.Keys)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileAt(source, language))!);
            Directory.CreateDirectory(Path.GetDirectoryName(FileAt(target, language))!);
            File.WriteAllBytes(FileAt(source, language), bytes);
            File.WriteAllBytes(FileAt(target, language), bytes);
        }
        File.WriteAllBytes(FileAt(source, "pt"), [0]);
        await Assert.ThrowsAsync<IOException>(() => ServerPresentation.ApplyValidatedAsync(source,target,"New",hashes,CancellationToken.None));
        Assert.Equal("New", Lookup(File.ReadAllBytes(FileAt(target,"en")), ServerPresentation.NameKey));
        Assert.False(File.Exists(Path.Combine(target,"krosforge-server.json")));
        File.WriteAllBytes(FileAt(source,"pt"),bytes);
        await ServerPresentation.ApplyValidatedAsync(source,target,"New",hashes,CancellationToken.None);
        var first = File.ReadAllBytes(FileAt(target,"en"));
        await ServerPresentation.ApplyValidatedAsync(source,target,"New",hashes,CancellationToken.None);
        Assert.Equal(first,File.ReadAllBytes(FileAt(target,"en")));
        await ServerPresentation.ApplyValidatedAsync(source,target,"Renamed",hashes,CancellationToken.None);
        foreach (var language in hashes.Keys)
        {
            Assert.Equal(bytes, File.ReadAllBytes(FileAt(source,language)));
            Assert.Equal(ServerPresentation.Rename(bytes,"Renamed"),File.ReadAllBytes(FileAt(target,language)));
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerPresentation.ApplyValidatedAsync(source,target,"Cancelled",hashes,cancellation.Token));
        Assert.Equal("Renamed", Lookup(File.ReadAllBytes(FileAt(target,"en")),ServerPresentation.NameKey));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryLinksCannotRedirectPersonalization(bool dangling)
    {
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
        var bytes = Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(FileAt(source, "en"))!);
        Directory.CreateDirectory(Path.GetDirectoryName(FileAt(target, "en"))!);
        File.WriteAllBytes(FileAt(source, "en"), bytes);
        File.WriteAllBytes(FileAt(target, "en"), bytes);
        var outside = Path.Combine(root, "outside");
        if (!dangling) File.WriteAllText(outside, "Preserve");
        File.CreateSymbolicLink(FileAt(target, "en") + ".krosforge-server.tmp", outside);
        var hashes = new Dictionary<string, string> { ["en"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        await Assert.ThrowsAsync<IOException>(() => ServerPresentation.ApplyValidatedAsync(source, target, "New", hashes, TestContext.Current.CancellationToken));
        if (dangling) Assert.False(File.Exists(outside));
        else Assert.Equal("Preserve", File.ReadAllText(outside));
        Assert.Equal(bytes, File.ReadAllBytes(FileAt(target, "en")));
    }
    private static Snapshot Snapshot(RuntimeService runtime) => runtime.Handle(new(Wire.Version,"snapshot")).Data!.Value.Deserialize<Snapshot>()!;
    private static string FileAt(string root,string language) => Path.Combine(root,"Dofus_Data","StreamingAssets","Content","I18n",language+".bin");
    private static byte[] Fixture()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream,Encoding.UTF8,true);
        writer.Write("fr"); writer.Write(2); writer.Write(ServerPresentation.NameKey); writer.Write(23); writer.Write(7); writer.Write(32);
        writer.Write("Original"); writer.Write("Other"); return stream.ToArray();
    }
    private static string Lookup(byte[] bytes,int key)
    {
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        reader.ReadString(); var count = reader.ReadInt32();
        for(var i=0;i<count;i++) { var id=reader.ReadInt32(); var offset=reader.ReadInt32(); if(id==key) { stream.Position=offset; return reader.ReadString(); } }
        throw new InvalidOperationException();
    }
    public void Dispose() { if(Directory.Exists(root)) Directory.Delete(root,true); }
}
