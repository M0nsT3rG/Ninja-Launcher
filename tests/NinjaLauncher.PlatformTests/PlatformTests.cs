using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Platform;

namespace NinjaLauncher.PlatformTests;

/// <summary>Runs on each CI operating system to check OS-specific behaviour.</summary>
public sealed class PlatformTests
{
    [Fact]
    public void Platform_detection_matches_runtime()
    {
        var p = PlatformInfo.Current;
        Assert.Equal(OperatingSystem.IsWindows(), p.IsWindows);
        Assert.Equal(OperatingSystem.IsMacOS(), p.IsMac);
        Assert.Equal(OperatingSystem.IsLinux(), p.IsLinux);
        Assert.Equal(OperatingSystem.IsWindows() ? ";" : ":", p.ClasspathSeparator);
        Assert.Contains(p.MojangArch, new[] { "x86", "x86_64", "arm64" });
    }

    [Fact]
    public void Default_paths_follow_os_conventions()
    {
        var old = Environment.GetEnvironmentVariable("NINJA_LAUNCHER_HOME");
        Environment.SetEnvironmentVariable("NINJA_LAUNCHER_HOME", null);
        try
        {
            var expected = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : OperatingSystem.IsMacOS() ? "Library/Application Support"
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? ".local/share";
            var paths = new AppPaths();
            Assert.Contains(expected.Replace('/', Path.DirectorySeparatorChar), paths.DataDirectory);
            Assert.EndsWith(AppPaths.AppFolderName, paths.DataDirectory);
        }
        finally { Environment.SetEnvironmentVariable("NINJA_LAUNCHER_HOME", old); }
    }

    [Fact]
    public void Unicode_and_spaces_in_data_root_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "Ninja Tést 世界 " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var paths = new AppPaths(root);
            Assert.True(Directory.Exists(paths.InstancesDirectory));
            Assert.StartsWith(root, paths.DatabasePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Secret_store_round_trips_or_reports_unavailable()
    {
        var root = Directory.CreateTempSubdirectory("ninja-secret-").FullName;
        try
        {
            var store = SecretStoreFactory.Create(new AppPaths(root));
            if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("CI") == "true")
                Assert.Skip("The CI login keychain may be locked; covered by manual testing.");
            var key = "test:" + Guid.NewGuid().ToString("N");
            await store.SetAsync(key, "s3cr3t-ü", TestContext.Current.CancellationToken);
            Assert.Equal("s3cr3t-ü", await store.GetAsync(key, TestContext.Current.CancellationToken));
            await store.DeleteAsync(key, TestContext.Current.CancellationToken);
            Assert.Null(await store.GetAsync(key, TestContext.Current.CancellationToken));
            if (OperatingSystem.IsWindows())
            {
                Assert.True(store.IsAvailable);
                // The on-disk blob must not contain the plaintext.
                await store.SetAsync(key, "plain-marker", TestContext.Current.CancellationToken);
                foreach (var f in Directory.GetFiles(Path.Combine(root, "secrets")))
                    Assert.DoesNotContain("plain-marker", await File.ReadAllTextAsync(f, TestContext.Current.CancellationToken));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Java_discovery_does_not_throw()
    {
        var root = Directory.CreateTempSubdirectory("ninja-java-").FullName;
        try
        {
            var found = await new JavaDiscovery(new AppPaths(root)).DiscoverAsync(TestContext.Current.CancellationToken);
            Assert.All(found, j => Assert.True(File.Exists(j.ExecutablePath)));
        }
        finally { Directory.Delete(root, true); }
    }
}
