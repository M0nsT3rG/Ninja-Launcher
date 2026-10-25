using System.IO.Compression;
using NinjaLauncher.Application.Security;

namespace NinjaLauncher.UnitTests;

public sealed class SecurityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ninja-sec-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    public void Combine_rejects_paths_outside_root(string relative) =>
        Assert.Throws<UnsafePathException>(() => SafePath.Combine(_root, relative));

    [Fact]
    public void Combine_allows_nested_and_unicode_paths()
    {
        var p = SafePath.Combine(_root, "saves/Mundo ñandú 世界/level.dat");
        Assert.StartsWith(Path.GetFullPath(_root), p);
    }

    [Fact]
    public void Combine_rejects_sibling_prefix_directory()
    {
        // "/tmp/root-evil" must not count as being under "/tmp/root".
        Assert.False(SafePath.IsUnder(_root, _root + "-evil" + Path.DirectorySeparatorChar + "x"));
    }

    [Theory]
    [InlineData("my/world:1", "my_world_1")]
    [InlineData("  ..  ", "unnamed")]
    [InlineData("ok name", "ok name")]
    public void SanitizeName_removes_separators(string input, string expected) => Assert.Equal(expected, SafePath.SanitizeName(input));

    [Fact]
    public void SanitizeName_keeps_extension_when_truncating()
    {
        var name = SafePath.SanitizeName(new string('a', 120) + ".jar");
        Assert.EndsWith(".jar", name);
        Assert.Equal(80, name.Length);
    }

    [Fact]
    public void Extract_blocks_zip_slip()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(a.CreateEntry("../../evil.txt").Open())) w.Write("x");
        Assert.Throws<UnsafePathException>(() => SafeArchive.ExtractToDirectory(zip, Path.Combine(_root, "out")));
        Assert.False(File.Exists(Path.Combine(_root, "..", "evil.txt")));
    }

    [Fact]
    public void Extract_enforces_size_limit()
    {
        var zip = Path.Combine(_root, "bomb.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var s = a.CreateEntry("big.bin", CompressionLevel.Optimal).Open()) s.Write(new byte[2_000_000]);
        Assert.Throws<InvalidDataException>(() => SafeArchive.ExtractToDirectory(zip, Path.Combine(_root, "out"), maxTotalBytes: 1_000_000));
    }

    [Fact]
    public void Extract_applies_filter_and_writes_files()
    {
        var zip = Path.Combine(_root, "ok.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(a.CreateEntry("lib/native.dll").Open())) w.Write("n");
            using (var w = new StreamWriter(a.CreateEntry("META-INF/MANIFEST.MF").Open())) w.Write("m");
        }
        var outDir = Path.Combine(_root, "out");
        SafeArchive.ExtractToDirectory(zip, outDir, name => !name.StartsWith("META-INF/"));
        Assert.True(File.Exists(Path.Combine(outDir, "lib", "native.dll")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "META-INF")));
    }

    [Theory]
    [InlineData("--accessToken eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk --version 1.21", "eyJhbGci")]
    [InlineData("refresh_token=M.C123_abc&scope=x", "M.C123_abc")]
    [InlineData("http://localhost:5000/?code=M.C1234-secret&state=abc", "M.C1234-secret")]
    [InlineData("{\"access_token\":\"opaque-value-1\"}", "opaque-value-1")]
    [InlineData("XBL3.0 x=1234;eyJ0b2tlbiI6dHJ1ZX0", "1234;")]
    public void Redactor_removes_secrets(string input, string secret) => Assert.DoesNotContain(secret, SecretRedactor.Redact(input));

    [Fact]
    public void Redactor_keeps_ordinary_text() =>
        Assert.Equal("Game exited with code 1", SecretRedactor.Redact("Game exited with code 1"));
}
