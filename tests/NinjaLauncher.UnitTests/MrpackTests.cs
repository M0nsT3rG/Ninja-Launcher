using System.IO.Compression;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.UnitTests;

public sealed class MrpackTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ninja-mrpack-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private string Pack(string filesJson, string deps = """{"minecraft":"1.21.1","fabric-loader":"0.16.5"}""", params (string Name, string Body)[] extra)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mrpack");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
            w.Write($$"""{"formatVersion":1,"game":"minecraft","versionId":"1.0","name":"Test Pack","files":{{filesJson}},"dependencies":{{deps}}}""");
        foreach (var (name, body) in extra)
            using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(body);
        return path;
    }

    private const string GoodFile = """{"path":"mods/a.jar","hashes":{"sha1":"aa","sha512":"bb"},"downloads":["https://cdn.modrinth.com/data/x/a.jar"],"fileSize":3}""";

    [Fact]
    public void Valid_pack_exposes_versions_and_loader()
    {
        var index = MrpackImporter.ReadIndex(Pack($"[{GoodFile}]"));
        Assert.Equal("1.21.1", index.MinecraftVersion);
        Assert.Equal((ModLoaderKind.Fabric, "0.16.5"), index.Loader);
    }

    [Theory]
    [InlineData("""{"path":"../evil.jar","hashes":{"sha1":"a","sha512":"b"},"downloads":["https://cdn.modrinth.com/x"],"fileSize":1}""")]
    [InlineData("""{"path":"mods/a.jar","hashes":{"sha1":"a","sha512":"b"},"downloads":["https://evil.example/x"],"fileSize":1}""")]
    [InlineData("""{"path":"mods/a.jar","hashes":{"sha1":"a","sha512":"b"},"downloads":["http://cdn.modrinth.com/x"],"fileSize":1}""")]
    [InlineData("""{"path":"mods/a.jar","hashes":{"sha1":"a"},"downloads":["https://cdn.modrinth.com/x"],"fileSize":1}""")]
    [InlineData("""{"path":"mods/a.jar","hashes":{"sha1":"a","sha512":"b"},"downloads":[],"fileSize":1}""")]
    public void Unsafe_or_incomplete_files_are_rejected(string file) =>
        Assert.ThrowsAny<Exception>(() => MrpackImporter.ReadIndex(Pack($"[{file}]")));

    [Fact]
    public void Missing_minecraft_dependency_is_rejected() =>
        Assert.Throws<InvalidDataException>(() => MrpackImporter.ReadIndex(Pack("[]", """{"fabric-loader":"1"}""")));

    [Fact]
    public async Task Overrides_are_applied_and_cannot_escape()
    {
        var pack = Pack("[]", extra: [("overrides/config/a.txt", "base"), ("client-overrides/config/a.txt", "client")]);
        var game = Path.Combine(_dir, "game");
        var importer = new MrpackImporter(null!);
        await importer.InstallContentAsync(pack, MrpackImporter.ReadIndex(pack), game, null, TestContext.Current.CancellationToken);
        Assert.Equal("client", await File.ReadAllTextAsync(Path.Combine(game, "config", "a.txt"), TestContext.Current.CancellationToken));

        var evil = Pack("[]", extra: [("overrides/../../escape.txt", "x")]);
        await Assert.ThrowsAsync<NinjaLauncher.Application.Security.UnsafePathException>(() =>
            importer.InstallContentAsync(evil, MrpackImporter.ReadIndex(evil), game, null, TestContext.Current.CancellationToken));
    }
}
