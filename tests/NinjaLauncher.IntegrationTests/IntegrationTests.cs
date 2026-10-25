using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.Downloads;
using NinjaLauncher.Infrastructure.Persistence;
using NinjaLauncher.Minecraft.Java.Install;
using NinjaLauncher.Minecraft.Java.Metadata;
using NinjaLauncher.Minecraft.Java.Runtime;
using NinjaLauncher.Mods.Loaders;
using NinjaLauncher.Mods.Modrinth;
using NinjaLauncher.Platform;

namespace NinjaLauncher.IntegrationTests;

/// <summary>
/// Real-network tests against Mojang, Fabric, Quilt and Modrinth (no mocks).
/// The multi-hundred-MB full install only runs when NINJA_FULL_INSTALL=1.
/// </summary>
public sealed class NetworkTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ninja-it-").FullName;
    private readonly AppPaths _paths;
    private readonly HttpDownloadService _downloads = new(HttpDownloadService.CreateClient(), () => 16, NullLogger<HttpDownloadService>.Instance);
    private readonly VersionCatalog _catalog;

    public NetworkTests()
    {
        _paths = new AppPaths(Environment.GetEnvironmentVariable("NINJA_IT_HOME") ?? _home);
        _catalog = new VersionCatalog(_downloads, _paths, NullLogger<VersionCatalog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Manifest_lists_latest_release_and_its_metadata_resolves()
    {
        var manifest = await _catalog.GetManifestAsync(ct: TestContext.Current.CancellationToken);
        Assert.Contains(manifest.Versions, v => v.Id == manifest.Latest.Release && v.Type == "release");
        var version = await _catalog.ResolveAsync(manifest.Latest.Release, TestContext.Current.CancellationToken);
        Assert.NotNull(version.MainClass);
        Assert.NotNull(version.AssetIndex);
        Assert.NotNull(version.JavaVersion);
        Assert.NotEmpty(new LibraryResolver(Path.Combine(_paths.GameDirectory, "libraries"), RuleEvaluator.Current).Resolve(version.Libraries));
    }

    [Fact]
    public async Task Fabric_and_Quilt_profiles_install_and_inherit_vanilla()
    {
        foreach (var provider in new[] { MetaProfileLoaderProvider.Fabric(_downloads, _catalog), MetaProfileLoaderProvider.Quilt(_downloads, _catalog) })
        {
            var versions = await provider.GetVersionsAsync("1.21.1", TestContext.Current.CancellationToken);
            Assert.NotEmpty(versions);
            var id = await provider.InstallAsync("1.21.1", versions[0].Version, null, TestContext.Current.CancellationToken);
            var resolved = await _catalog.ResolveAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(id, resolved.Id);
            Assert.Equal(id, resolved.Jar);
            Assert.NotEqual("net.minecraft.client.main.Main", resolved.MainClass);
            Assert.Equal(21, resolved.JavaVersion!.MajorVersion);
        }
    }

    [Fact]
    public async Task NeoForge_and_Forge_version_lists_are_available()
    {
        var neo = new InstallerLoaderProvider(ModLoaderKind.NeoForge, _downloads, _paths, _catalog, null!, null!, NullLogger<InstallerLoaderProvider>.Instance);
        Assert.NotEmpty(await neo.GetVersionsAsync("1.21.1", TestContext.Current.CancellationToken));
        var forge = new InstallerLoaderProvider(ModLoaderKind.Forge, _downloads, _paths, _catalog, null!, null!, NullLogger<InstallerLoaderProvider>.Instance);
        var forgeVersions = await forge.GetVersionsAsync("1.20.1", TestContext.Current.CancellationToken);
        Assert.All(forgeVersions, v => Assert.StartsWith("1.20.1-", v.Version));
        Assert.NotEmpty(forgeVersions);
    }

    [Fact]
    public async Task Modrinth_search_and_versions_respect_filters()
    {
        var client = new ModrinthClient(_downloads);
        var result = await client.SearchAsync("sodium", ProjectKind.Mod, "1.21.1", ModLoaderKind.Fabric, 0, 5, TestContext.Current.CancellationToken);
        Assert.NotEmpty(result.Hits);
        var versions = await client.GetVersionsAsync(result.Hits[0].ProjectId, "1.21.1", ModLoaderKind.Fabric, TestContext.Current.CancellationToken);
        Assert.All(versions, v => Assert.Contains("1.21.1", v.GameVersions));
        Assert.All(versions, v => Assert.Contains("fabric", v.Loaders));
    }

    [Fact]
    public async Task Modrinth_install_downloads_mod_with_required_dependencies()
    {
        var client = new ModrinthClient(_downloads);
        var hit = (await client.SearchAsync("sodium extra", ProjectKind.Mod, "1.21.1", ModLoaderKind.Fabric, 0, 1, TestContext.Current.CancellationToken)).Hits[0];
        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "t", MinecraftVersion = "1.21.1", Loader = ModLoaderKind.Fabric, LoaderVersion = "x" };
        var game = Path.Combine(_home, "inst");
        var installed = await client.InstallAsync(hit, ProjectKind.Mod, instance, game, TestContext.Current.CancellationToken);
        Assert.NotEmpty(installed);
        Assert.All(installed, i => Assert.True(File.Exists(Path.Combine(game, "mods", i.FileName))));
        var before = Directory.GetFiles(Path.Combine(game, "mods")).Length;
        await client.InstallAsync(hit, ProjectKind.Mod, instance, game, TestContext.Current.CancellationToken); // reinstall must not duplicate
        Assert.Equal(before, Directory.GetFiles(Path.Combine(game, "mods")).Length);
    }

    [Fact]
    public async Task Outdated_mod_is_detected_and_updated_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new ModrinthClient(_downloads);
        var versions = await client.GetVersionsAsync("AANobbMI", "1.21.1", ModLoaderKind.Fabric, ct); // Sodium
        var oldest = versions.OrderBy(v => v.DatePublished).First(v => v.VersionType == "release");
        var file = oldest.Files.First(f => f.Primary);
        var game = Path.Combine(_home, "upd");
        var oldPath = Path.Combine(game, "mods", file.Filename + ".disabled"); // disabled state must survive the update
        await _downloads.DownloadAsync([new DownloadItem(new Uri(file.Url), oldPath, file.Hashes["sha1"], file.Size)], null, ct);

        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "u", MinecraftVersion = "1.21.1", Loader = ModLoaderKind.Fabric, LoaderVersion = "x" };
        var update = Assert.Single(await client.CheckUpdatesAsync(instance, game, ct));
        Assert.Equal(oldest.VersionNumber, update.CurrentVersion);
        var newName = await client.ApplyUpdateAsync(update, game, ct);
        Assert.EndsWith(".disabled", newName);
        Assert.False(File.Exists(oldPath));
        Assert.Empty(await client.CheckUpdatesAsync(instance, game, ct));
    }

    [Fact]
    public async Task A_real_modrinth_modpack_passes_validation()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new ModrinthClient(_downloads);
        var pack = (await client.SearchAsync("fabulously optimized", ProjectKind.Modpack, null, ModLoaderKind.None, 0, 1, ct)).Hits[0];
        var version = (await client.GetVersionsAsync(pack.ProjectId, null, ModLoaderKind.None, ct)).OrderByDescending(v => v.DatePublished).First();
        var file = version.Files.First(f => f.Filename.EndsWith(".mrpack"));
        var local = Path.Combine(_home, "pack.mrpack");
        await _downloads.DownloadAsync([new DownloadItem(new Uri(file.Url), local, file.Hashes["sha1"], file.Size)], null, ct);
        var index = MrpackImporter.ReadIndex(local);
        Assert.NotEmpty(index.Files);
        Assert.NotEqual(ModLoaderKind.None, index.Loader.Kind);
    }

    [Fact]
    public async Task Mojang_publishes_a_runtime_index_entry_for_this_platform()
    {
        var json = await _downloads.GetStringAsync(JavaRuntimeManager.RuntimeIndexUrl, TestContext.Current.CancellationToken);
        Assert.Contains($"\"{PlatformInfo.Current.JavaRuntimeKey}\"", json);
    }

    [Fact]
    public async Task Downloader_rejects_corrupted_content_by_hash()
    {
        var target = Path.Combine(_home, "bad.json");
        var item = new DownloadItem(VersionCatalog.ManifestUrl, target, "0000000000000000000000000000000000000000", null);
        await Assert.ThrowsAsync<DownloadFailedException>(() => _downloads.DownloadAsync([item], null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Full_install_of_a_release_and_its_java_runtime()
    {
        if (Environment.GetEnvironmentVariable("NINJA_FULL_INSTALL") != "1") Assert.Skip("Set NINJA_FULL_INSTALL=1 to download a full game version (~600 MB).");
        var ct = TestContext.Current.CancellationToken;
        var id = Environment.GetEnvironmentVariable("NINJA_FULL_INSTALL_VERSION") ?? "1.21.1";
        var version = await _catalog.ResolveAsync(id, ct);
        var installer = new GameInstaller(_downloads, _paths, NullLogger<GameInstaller>.Instance);
        var sw = Stopwatch.StartNew();
        var installed = await installer.InstallAsync(version, Path.Combine(_home, "inst"), null, ct);
        var installTime = sw.Elapsed;
        Assert.All(installed.Classpath, p => Assert.True(File.Exists(p), p));

        var runtimes = new JavaRuntimeManager(_downloads, _paths, new JavaDiscovery(_paths), NullLogger<JavaRuntimeManager>.Instance);
        var java = await runtimes.EnsureRuntimeAsync(version.JavaVersion, null, ct);
        var psi = new ProcessStartInfo(java.Replace("javaw.exe", "java.exe")) { RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-version");
        using var p = Process.Start(psi)!;
        var text = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        Assert.Equal(0, p.ExitCode);
        Assert.Contains(version.JavaVersion!.MajorVersion.ToString(), text);

        // Second pass must be a no-op (cache reuse).
        sw.Restart();
        await installer.InstallAsync(version, Path.Combine(_home, "inst"), null, ct);
        TestContext.Current.SendDiagnosticMessage($"Install {id}: {installTime.TotalSeconds:0.0}s; verify-only pass: {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.True(sw.Elapsed < installTime);
    }
}

/// <summary>
/// Engine smoke test: starts the real client with the generated command line and stops it once the game has
/// initialised (classpath, natives, assets and arguments are correct). It uses a placeholder session, so it is NOT
/// the authenticated acceptance test and proves nothing about sign-in or online play. Opt-in: NINJA_LAUNCH_SMOKE=1.
/// </summary>
public sealed class LaunchSmokeTests
{
    [Fact]
    public async Task Generated_command_line_boots_the_client()
    {
        if (Environment.GetEnvironmentVariable("NINJA_LAUNCH_SMOKE") != "1") Assert.Skip("Set NINJA_LAUNCH_SMOKE=1 (and NINJA_IT_HOME to a folder with an installed version).");
        var ct = TestContext.Current.CancellationToken;
        var paths = new AppPaths(Environment.GetEnvironmentVariable("NINJA_IT_HOME"));
        var downloads = new HttpDownloadService(HttpDownloadService.CreateClient(), () => 16, NullLogger<HttpDownloadService>.Instance);
        var catalog = new VersionCatalog(downloads, paths, NullLogger<VersionCatalog>.Instance);
        var id = Environment.GetEnvironmentVariable("NINJA_FULL_INSTALL_VERSION") ?? "1.21.1";
        var version = await catalog.ResolveAsync(id, ct);
        var gameDir = Path.Combine(paths.InstancesDirectory, "smoke-" + id);
        var installed = await new GameInstaller(downloads, paths, NullLogger<GameInstaller>.Instance).InstallAsync(version, gameDir, null, ct);
        var java = await new JavaRuntimeManager(downloads, paths, new JavaDiscovery(paths), NullLogger<JavaRuntimeManager>.Instance).EnsureRuntimeAsync(version.JavaVersion, null, ct);
        var args = NinjaLauncher.Minecraft.Java.Launch.LaunchArgumentBuilder.Build(installed,
            new("NinjaSmoke", "00000000000000000000000000000000", "0", "0"), new(gameDir, 512, 2048, ""));
        Directory.CreateDirectory(gameDir);

        var lines = new List<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        var booted = false;
        try
        {
            await NinjaLauncher.Minecraft.Java.Launch.GameProcess.RunAsync(java, args, gameDir, line =>
            {
                lock (lines) lines.Add(line.Text);
                // "Backend library" is logged once LWJGL/GLFW are loaded; "Reloading ResourceManager" once assets load.
                if (line.Text.Contains("Backend library") || line.Text.Contains("Reloading ResourceManager")) { booted = true; stop.CancelAfter(TimeSpan.FromSeconds(5)); }
            }, null, stop.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        lock (lines) TestContext.Current.SendDiagnosticMessage(string.Join('\n', lines.TakeLast(25)));
        Assert.True(booted, "Client did not reach LWJGL/resource initialisation. Last lines:\n" + string.Join('\n', lines.TakeLast(25)));
    }
}

/// <summary>Runs the official NeoForge/Forge installers headless. Opt-in (NINJA_LOADER_INSTALL=1, NINJA_IT_HOME): slow and heavy.</summary>
public sealed class InstallerLoaderTests
{
    [Theory]
    [InlineData(ModLoaderKind.NeoForge, "1.21.1")]
    [InlineData(ModLoaderKind.Forge, "1.20.1")]
    public async Task Official_installer_produces_a_resolvable_profile(ModLoaderKind kind, string mc)
    {
        if (Environment.GetEnvironmentVariable("NINJA_LOADER_INSTALL") != "1") Assert.Skip("Set NINJA_LOADER_INSTALL=1 and NINJA_IT_HOME to run loader installers.");
        var ct = TestContext.Current.CancellationToken;
        var paths = new AppPaths(Environment.GetEnvironmentVariable("NINJA_IT_HOME"));
        var downloads = new HttpDownloadService(HttpDownloadService.CreateClient(), () => 16, NullLogger<HttpDownloadService>.Instance);
        var catalog = new VersionCatalog(downloads, paths, NullLogger<VersionCatalog>.Instance);
        var installer = new GameInstaller(downloads, paths, NullLogger<GameInstaller>.Instance);
        var runtimes = new JavaRuntimeManager(downloads, paths, new JavaDiscovery(paths), NullLogger<JavaRuntimeManager>.Instance);
        var provider = new InstallerLoaderProvider(kind, downloads, paths, catalog, installer, runtimes, NullLogger<InstallerLoaderProvider>.Instance);

        var version = (await provider.GetVersionsAsync(mc, ct)).First(v => v.Stable).Version;
        var id = await provider.InstallAsync(mc, version, null, ct);
        var resolved = await catalog.ResolveAsync(id, ct);
        Assert.Equal(id, resolved.Jar);
        var installed = await installer.InstallAsync(resolved, Path.Combine(paths.InstancesDirectory, $"loader-{kind}"), null, ct);
        Assert.All(installed.Classpath, p => Assert.True(File.Exists(p), p));
        Assert.False(File.Exists(Path.Combine(paths.GameDirectory, "launcher_profiles.json")), "stub profile must be cleaned up");
    }
}

public sealed class SqliteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ninja-db-").FullName;
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Repositories_round_trip_and_migrations_are_idempotent()
    {
        var path = Path.Combine(_dir, "ninja.db");
        var db = new NinjaDatabase(path);
        var instances = new SqliteInstanceRepository(db);
        var instance = new GameInstance { Id = Guid.NewGuid(), Name = "ÃœnÃ¯code ä¸–ç•Œ", MinecraftVersion = "1.21.1", Loader = ModLoaderKind.Fabric, LoaderVersion = "0.16" };
        await instances.UpsertAsync(instance, TestContext.Current.CancellationToken);
        await instances.UpsertAsync(instance with { MaxMemoryMb = 8192 }, TestContext.Current.CancellationToken);

        db = new NinjaDatabase(path); // reopen: migration must not fail or wipe data
        var loaded = await new SqliteInstanceRepository(db).GetAsync(instance.Id, TestContext.Current.CancellationToken);
        Assert.Equal(8192, loaded!.MaxMemoryMb);
        Assert.Equal("ÃœnÃ¯code ä¸–ç•Œ", loaded.Name);

        var accounts = new SqliteAccountRepository(db);
        await accounts.UpsertAsync(new MinecraftAccount { ProfileId = "a", Username = "A" }, TestContext.Current.CancellationToken);
        await accounts.UpsertAsync(new MinecraftAccount { ProfileId = "b", Username = "B" }, TestContext.Current.CancellationToken);
        await accounts.SetActiveAsync("b", TestContext.Current.CancellationToken);
        var list = await accounts.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("b", Assert.Single(list, a => a.IsActive).ProfileId);

        var settings = new SqliteSettingsStore(db);
        await settings.SaveAsync(new LauncherSettings { Theme = ThemeMode.Light, DownloadConcurrency = 4 }, TestContext.Current.CancellationToken);
        Assert.Equal(ThemeMode.Light, (await settings.LoadAsync(TestContext.Current.CancellationToken)).Theme);

        var activity = new SqliteActivityLog(db);
        for (var i = 0; i < 3; i++) await activity.AddAsync($"event {i}", TestContext.Current.CancellationToken);
        Assert.Equal("event 2", (await activity.RecentAsync(1, TestContext.Current.CancellationToken))[0].Message);
    }
}
