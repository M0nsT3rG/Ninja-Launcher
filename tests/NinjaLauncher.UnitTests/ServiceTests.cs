using System.Net;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.Auth;
using NinjaLauncher.Infrastructure.Downloads;
using NinjaLauncher.Mods.Loaders;
using NinjaLauncher.Mods.Modrinth;
using NinjaLauncher.Platform;

namespace NinjaLauncher.UnitTests;

internal sealed class MemoryInstances : IInstanceRepository
{
    public readonly Dictionary<Guid, GameInstance> Items = new();
    public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GameInstance>>(Items.Values.ToList());
    public Task<GameInstance?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(id));
    public Task UpsertAsync(GameInstance instance, CancellationToken ct = default) { Items[instance.Id] = instance; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
}

internal sealed class MemoryAccounts : IAccountRepository
{
    public readonly Dictionary<string, MinecraftAccount> Items = new();
    public Task<IReadOnlyList<MinecraftAccount>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MinecraftAccount>>(Items.Values.ToList());
    public Task UpsertAsync(MinecraftAccount a, CancellationToken ct = default) { Items[a.ProfileId] = a with { IsActive = Items.GetValueOrDefault(a.ProfileId)?.IsActive ?? false }; return Task.CompletedTask; }
    public Task DeleteAsync(string id, CancellationToken ct = default) { Items.Remove(id); return Task.CompletedTask; }
    public Task SetActiveAsync(string id, CancellationToken ct = default)
    {
        foreach (var k in Items.Keys.ToList()) Items[k] = Items[k] with { IsActive = k == id };
        return Task.CompletedTask;
    }
}

internal sealed class NullActivity : IActivityLog
{
    public Task AddAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ActivityEntry>> RecentAsync(int count, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ActivityEntry>>([]);
}

public sealed class InstanceServiceTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ninja-inst-").FullName;
    private readonly MemoryInstances _repo = new();
    private readonly AppPaths _paths;
    private readonly InstanceService _service;

    public InstanceServiceTests()
    {
        _paths = new AppPaths(_home);
        _service = new InstanceService(_repo, _paths, new NullActivity());
    }

    public void Dispose() => Directory.Delete(_home, true);

    [Fact]
    public async Task Instances_get_isolated_directories_and_clone_copies_data()
    {
        var a = await _service.CreateAsync("Survival", "1.21.1");
        var dirA = _service.GetGameDirectory(a);
        Directory.CreateDirectory(Path.Combine(dirA, "saves", "World"));
        await File.WriteAllTextAsync(Path.Combine(dirA, "options.txt"), "fov:90");

        var b = await _service.CloneAsync(a.Id, "Survival copy");
        Assert.NotEqual(dirA, _service.GetGameDirectory(b));
        Assert.Equal("fov:90", await File.ReadAllTextAsync(Path.Combine(_service.GetGameDirectory(b), "options.txt")));

        await File.WriteAllTextAsync(Path.Combine(_service.GetGameDirectory(b), "options.txt"), "fov:70");
        Assert.Equal("fov:90", await File.ReadAllTextAsync(Path.Combine(dirA, "options.txt")));
    }

    [Fact]
    public async Task Delete_removes_only_that_instance()
    {
        var a = await _service.CreateAsync("A", "1.21.1");
        var b = await _service.CreateAsync("B", "1.21.1");
        var outside = Path.Combine(_home, "keep.txt");
        await File.WriteAllTextAsync(outside, "x");
        await _service.DeleteAsync(a.Id);
        Assert.False(Directory.Exists(_service.GetGameDirectory(a)));
        Assert.True(Directory.Exists(_service.GetGameDirectory(b)));
        Assert.True(File.Exists(outside));
        Assert.Single(_repo.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public async Task Invalid_names_are_rejected(string name) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(name, "1.21.1"));

    [Fact]
    public async Task Update_validates_memory()
    {
        var a = await _service.CreateAsync("A", "1.21.1");
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateAsync(a with { MinMemoryMb = 4096, MaxMemoryMb = 1024 }));
    }

    [Fact]
    public async Task Backup_and_restore_round_trip_without_overwriting()
    {
        var a = await _service.CreateAsync("A", "1.21.1");
        var world = Path.Combine(_service.GetGameDirectory(a), "saves", "My World");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "data");

        var zip = await _service.BackupWorldAsync(a, "My World");
        await Assert.ThrowsAsync<IOException>(() => _service.RestoreBackupAsync(a, zip)); // world still exists

        Directory.Delete(world, true);
        await _service.RestoreBackupAsync(a, zip);
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(world, "level.dat")));
    }

    [Fact]
    public async Task Restore_refuses_files_outside_backups_directory()
    {
        var a = await _service.CreateAsync("A", "1.21.1");
        await Assert.ThrowsAsync<NinjaLauncher.Application.Security.UnsafePathException>(() => _service.RestoreBackupAsync(a, Path.Combine(_home, "x.zip")));
    }
}

public sealed class AccountServiceTests
{
    private sealed class FakeAuth : IMinecraftAuthenticator
    {
        public string Profile = "abc";
        public int Refreshes;
        public bool IsConfigured => true;
        public Task<MinecraftSignIn> SignInInteractiveAsync(CancellationToken ct) =>
            Task.FromResult(new MinecraftSignIn("abc", "Alex", "1", "mc-token", DateTimeOffset.UtcNow.AddHours(1), "refresh-1"));
        public Task<MinecraftSignIn> RefreshAsync(string refresh, CancellationToken ct)
        {
            Refreshes++;
            return Task.FromResult(new MinecraftSignIn(Profile, "Alex", "1", "mc-token-2", DateTimeOffset.UtcNow.AddHours(1), refresh + "-rotated"));
        }
    }

    [Fact]
    public async Task Sign_in_stores_refresh_token_securely_and_activates_account()
    {
        var secrets = new SessionOnlySecretStore();
        var repo = new MemoryAccounts();
        var service = new AccountService(repo, secrets, new FakeAuth(), new NullActivity());
        var account = await service.SignInAsync();
        Assert.True(repo.Items["abc"].IsActive);
        Assert.Equal("refresh-1", await secrets.GetAsync("msa-refresh:abc"));
        Assert.DoesNotContain("refresh", System.Text.Json.JsonSerializer.Serialize(repo.Items["abc"]), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Alex", account.Username);
    }

    [Fact]
    public async Task Session_is_cached_then_refreshed_and_profile_mismatch_rejected()
    {
        var secrets = new SessionOnlySecretStore();
        var auth = new FakeAuth();
        var repo = new MemoryAccounts();
        var service = new AccountService(repo, secrets, auth, new NullActivity());
        var account = await service.SignInAsync();
        await service.GetSessionAsync(account);
        Assert.Equal(0, auth.Refreshes);

        var fresh = new AccountService(repo, secrets, auth, new NullActivity()); // new process: no cached session
        var s = await fresh.GetSessionAsync(account);
        Assert.Equal("mc-token-2", s.MinecraftAccessToken);
        Assert.Equal("refresh-1-rotated", await secrets.GetAsync("msa-refresh:abc"));

        auth.Profile = "someone-else";
        var third = new AccountService(repo, secrets, auth, new NullActivity());
        await Assert.ThrowsAsync<AuthenticationException>(() => third.GetSessionAsync(account));
    }

    [Fact]
    public async Task Remove_deletes_secret_and_record()
    {
        var secrets = new SessionOnlySecretStore();
        var repo = new MemoryAccounts();
        var service = new AccountService(repo, secrets, new FakeAuth(), new NullActivity());
        var account = await service.SignInAsync();
        await service.RemoveAsync(account.ProfileId);
        Assert.Null(await secrets.GetAsync("msa-refresh:abc"));
        Assert.Empty(repo.Items);
        await Assert.ThrowsAsync<AuthenticationException>(() => new AccountService(repo, secrets, new FakeAuth(), new NullActivity()).GetSessionAsync(account));
    }
}

public sealed class MiscTests
{
    [Theory]
    [InlineData(2148916233L, "no Xbox profile")]
    [InlineData(2148916238L, "child account")]
    [InlineData(2148916235L, "country")]
    public void Xsts_errors_are_explained(long xerr, string expected)
    {
        var e = MicrosoftAuthenticator.MapError("https://xsts.auth.xboxlive.com/xsts/authorize", HttpStatusCode.Unauthorized, $"{{\"XErr\":{xerr}}}");
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Unapproved_app_registration_is_explained() =>
        Assert.Contains("approved by Mojang", MicrosoftAuthenticator.MapError("https://api.minecraftservices.com/authentication/login_with_xbox", HttpStatusCode.Forbidden, "{}").Message);

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.20.4", "20.4.")]
    [InlineData("1.20.1", null)]
    [InlineData("1.19.2", null)]
    [InlineData("26.1", "26.1.0.")]
    [InlineData("26.1.2", "26.1.2.")]
    [InlineData("25w14craftmine", null)]
    public void NeoForge_version_prefixes(string mc, string? prefix) => Assert.Equal(prefix, InstallerLoaderProvider.NeoForgePrefix(mc));

    [Theory]
    [InlineData("1.12.2", false)]
    [InlineData("1.12.1", true)]
    [InlineData("1.7.10", true)]
    [InlineData("1.20.1", false)]
    public void Forge_support_boundary(string mc, bool unsupported) => Assert.Equal(unsupported, InstallerLoaderProvider.ForgeUnsupported(mc));

    [Theory]
    [InlineData("1.8.0_391", 8)]
    [InlineData("17.0.12", 17)]
    [InlineData("21", 21)]
    [InlineData("25.0.1+9", 25)]
    public void Java_major_version_parsing(string version, int major) => Assert.Equal(major, JavaDiscovery.ParseMajor(version));

    [Fact]
    public void Java_release_file_is_read()
    {
        var home = Directory.CreateTempSubdirectory("ninja-jdk-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), "");
            File.WriteAllText(Path.Combine(home, "release"), "IMPLEMENTOR=\"Test\"\nJAVA_VERSION=\"21.0.4\"\n");
            var j = JavaDiscovery.TryRead(home, "Test");
            Assert.NotNull(j);
            Assert.Equal(21, j.MajorVersion);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void Mods_can_be_disabled_and_enabled()
    {
        var game = Directory.CreateTempSubdirectory("ninja-mods-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "mods"));
            File.WriteAllText(Path.Combine(game, "mods", "sodium.jar"), "");
            ModrinthClient.SetModEnabled(game, "sodium.jar", false);
            Assert.Equal([("sodium.jar.disabled", false)], ModrinthClient.ListMods(game));
            ModrinthClient.SetModEnabled(game, "sodium.jar.disabled", true);
            Assert.Equal([("sodium.jar", true)], ModrinthClient.ListMods(game));
            Assert.Throws<NinjaLauncher.Application.Security.UnsafePathException>(() => ModrinthClient.SetModEnabled(game, "../x.jar", false));
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void Downloaded_file_validation_checks_size_and_hash()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "hello");
            const string sha1 = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d";
            Assert.True(HttpDownloadService.IsValid(new DownloadItem(new Uri("https://x/y"), file, sha1, 5), fullVerification: true));
            Assert.False(HttpDownloadService.IsValid(new DownloadItem(new Uri("https://x/y"), file, "00", 5), fullVerification: true));
            Assert.True(HttpDownloadService.IsValid(new DownloadItem(new Uri("https://x/y"), file, "00", 5), fullVerification: false)); // fast path: size only
            Assert.False(HttpDownloadService.IsValid(new DownloadItem(new Uri("https://x/y"), file, sha1, 6), fullVerification: false));
        }
        finally { File.Delete(file); }
    }
}
