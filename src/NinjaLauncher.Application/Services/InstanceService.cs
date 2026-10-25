using System.IO.Compression;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Application.Services;

/// <summary>Instance lifecycle. Each instance owns <c>InstancesDirectory/&lt;id&gt;</c>; nothing outside it is ever modified.</summary>
public sealed class InstanceService(IInstanceRepository repository, IAppPaths paths, IActivityLog activity)
{
    public string GetGameDirectory(GameInstance instance) => SafePath.Combine(paths.InstancesDirectory, instance.Id.ToString("N"));

    public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken ct = default) => repository.ListAsync(ct);

    public Task<GameInstance?> GetAsync(Guid id, CancellationToken ct = default) => repository.GetAsync(id, ct);

    public async Task<GameInstance> CreateAsync(string name, string minecraftVersion, ModLoaderKind loader = ModLoaderKind.None,
        string? loaderVersion = null, int maxMemoryMb = 4096, CancellationToken ct = default)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(minecraftVersion)) throw new ArgumentException("A Minecraft version is required.");
        if (loader != ModLoaderKind.None && string.IsNullOrWhiteSpace(loaderVersion)) throw new ArgumentException("A loader version is required.");
        var instance = new GameInstance
        {
            Id = Guid.NewGuid(), Name = name.Trim(), MinecraftVersion = minecraftVersion,
            Loader = loader, LoaderVersion = loader == ModLoaderKind.None ? null : loaderVersion, MaxMemoryMb = maxMemoryMb,
        };
        Directory.CreateDirectory(GetGameDirectory(instance));
        await repository.UpsertAsync(instance, ct);
        await activity.AddAsync($"Created instance '{instance.Name}' ({minecraftVersion})", ct);
        return instance;
    }

    public async Task<GameInstance> UpdateAsync(GameInstance instance, CancellationToken ct = default)
    {
        ValidateName(instance.Name);
        if (instance.MinMemoryMb < 256 || instance.MaxMemoryMb < instance.MinMemoryMb)
            throw new ArgumentException("Memory must be at least 256 MB and maximum must not be below minimum.");
        if (instance.JavaPath is { Length: > 0 } java && !File.Exists(java))
            throw new ArgumentException($"Java executable not found: {java}");
        var normalized = instance with { Name = instance.Name.Trim() };
        await repository.UpsertAsync(normalized, ct);
        return normalized;
    }

    public async Task<GameInstance> RenameAsync(Guid id, string newName, CancellationToken ct = default)
    {
        var instance = await GetRequiredAsync(id, ct);
        return await UpdateAsync(instance with { Name = newName }, ct);
    }

    public async Task<GameInstance> CloneAsync(Guid id, string newName, CancellationToken ct = default)
    {
        ValidateName(newName);
        var source = await GetRequiredAsync(id, ct);
        var clone = source with { Id = Guid.NewGuid(), Name = newName.Trim(), CreatedUtc = DateTimeOffset.UtcNow, LastPlayedUtc = null };
        await Task.Run(() => CopyDirectory(GetGameDirectory(source), GetGameDirectory(clone), ct), ct);
        await repository.UpsertAsync(clone, ct);
        await activity.AddAsync($"Cloned '{source.Name}' to '{clone.Name}'", ct);
        return clone;
    }

    /// <summary>Deletes the instance record and its own directory only. Callers must confirm with the user first.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await GetRequiredAsync(id, ct);
        var dir = GetGameDirectory(instance);
        if (Directory.Exists(dir)) await Task.Run(() => Directory.Delete(dir, recursive: true), ct);
        await repository.DeleteAsync(id, ct);
        await activity.AddAsync($"Deleted instance '{instance.Name}'", ct);
    }

    public Task<long> GetDiskUsageAsync(GameInstance instance, CancellationToken ct = default) => Task.Run(() =>
    {
        var dir = new DirectoryInfo(GetGameDirectory(instance));
        if (!dir.Exists) return 0L;
        long total = 0;
        foreach (var file in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            total += file.Length;
        }
        return total;
    }, ct);

    public IReadOnlyList<string> ListWorlds(GameInstance instance)
    {
        var saves = Path.Combine(GetGameDirectory(instance), "saves");
        return Directory.Exists(saves) ? Directory.GetDirectories(saves).Select(Path.GetFileName).OfType<string>().Order().ToList() : [];
    }

    public async Task<string> BackupWorldAsync(GameInstance instance, string worldName, CancellationToken ct = default)
    {
        var world = SafePath.Combine(Path.Combine(GetGameDirectory(instance), "saves"), worldName);
        if (!Directory.Exists(world)) throw new DirectoryNotFoundException($"World '{worldName}' not found.");
        var backupDir = Path.Combine(paths.BackupsDirectory, instance.Id.ToString("N"));
        Directory.CreateDirectory(backupDir);
        var file = Path.Combine(backupDir, $"{SafePath.SanitizeName(worldName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() => ZipFile.CreateFromDirectory(world, file, CompressionLevel.Optimal, includeBaseDirectory: true), ct);
        await activity.AddAsync($"Backed up world '{worldName}' of '{instance.Name}'", ct);
        return file;
    }

    public IReadOnlyList<string> ListBackups(GameInstance instance)
    {
        var dir = Path.Combine(paths.BackupsDirectory, instance.Id.ToString("N"));
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.zip").OrderDescending().ToList() : [];
    }

    /// <summary>Restores a backup into the saves folder. Refuses to overwrite an existing world folder.</summary>
    public async Task RestoreBackupAsync(GameInstance instance, string backupZip, CancellationToken ct = default)
    {
        if (!SafePath.IsUnder(paths.BackupsDirectory, backupZip)) throw new UnsafePathException("Backup must come from the backups directory.");
        var saves = Path.Combine(GetGameDirectory(instance), "saves");
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(backupZip);
            var roots = zip.Entries.Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).Distinct().ToList();
            foreach (var root in roots)
                if (Directory.Exists(SafePath.Combine(saves, root)))
                    throw new IOException($"World folder '{root}' already exists; rename or back it up first.");
            SafeArchive.Extract(zip, saves);
        }, ct);
        await activity.AddAsync($"Restored backup {Path.GetFileName(backupZip)} into '{instance.Name}'", ct);
    }

    public async Task MarkPlayedAsync(Guid id, CancellationToken ct = default)
    {
        var instance = await GetRequiredAsync(id, ct);
        await repository.UpsertAsync(instance with { LastPlayedUtc = DateTimeOffset.UtcNow }, ct);
    }

    private async Task<GameInstance> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await repository.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Instance {id} not found.");

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Instance name cannot be empty.");
        if (name.Trim().Length > 64) throw new ArgumentException("Instance name must be 64 characters or fewer.");
    }

    internal static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source)) return;
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(file).LinkTarget is not null) continue; // never follow symlinks out of the instance
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
        }
    }
}
