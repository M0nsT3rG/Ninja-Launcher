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
        // Write to a temp name so a failure never leaves a truncated zip that looks like a valid backup.
        var temp = file + ".tmp";
        try
        {
            await Task.Run(() => ZipWorld(world, temp, ct), ct);
            File.Move(temp, file);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        await activity.AddAsync($"Backed up world '{worldName}' of '{instance.Name}'", ct);
        return file;
    }

    private static void ZipWorld(string world, string zipPath, CancellationToken ct)
    {
        var root = Path.GetFileName(Path.TrimEndingDirectorySeparator(world));
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var path in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(path) == "session.lock") continue; // held open by a running game; not world data
            var name = root + "/" + Path.GetRelativePath(world, path).Replace('\\', '/');
            zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
        }
    }

    public const long MaxIconBytes = 2 * 1024 * 1024;
    private static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    /// <summary>Full path of the instance's custom icon, or null when it uses the default.</summary>
    public string? GetIconPath(GameInstance instance)
    {
        if (instance.IconKey is not { } key) return null;
        var path = SafePath.Combine(GetGameDirectory(instance), key);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Copies an image (PNG/JPG/WebP/GIF, at most 2 MB) into the instance folder and uses it as the icon.</summary>
    public async Task<GameInstance> SetIconAsync(Guid id, string imagePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (!IconExtensions.Contains(ext)) throw new ArgumentException("Pick a PNG, JPG, WebP or GIF image.");
        if (new FileInfo(imagePath).Length > MaxIconBytes) throw new ArgumentException("Icons must be 2 MB or smaller.");
        return await SetIconBytesAsync(id, await File.ReadAllBytesAsync(imagePath, ct), ext, ct);
    }

    public async Task<GameInstance> SetIconBytesAsync(Guid id, byte[] image, string extension, CancellationToken ct = default)
    {
        if (image.Length > MaxIconBytes) throw new ArgumentException("Icons must be 2 MB or smaller.");
        if (!IconExtensions.Contains(extension)) extension = ".png";
        var current = await GetRequiredAsync(id, ct);
        var dir = GetGameDirectory(current);
        Directory.CreateDirectory(dir);
        foreach (var old in IconExtensions) File.Delete(Path.Combine(dir, "instance-icon" + old));
        var key = "instance-icon" + extension;
        await File.WriteAllBytesAsync(Path.Combine(dir, key), image, ct);
        var updated = current with { IconKey = key };
        await repository.UpsertAsync(updated, ct);
        return updated;
    }

    public async Task<GameInstance> ClearIconAsync(Guid id, CancellationToken ct = default)
    {
        var current = await GetRequiredAsync(id, ct);
        if (GetIconPath(current) is { } path) File.Delete(path);
        var updated = current with { IconKey = null };
        await repository.UpsertAsync(updated, ct);
        return updated;
    }

    /// <summary>Records the installed loader profile without overwriting other fields edited meanwhile.</summary>
    public async Task<GameInstance> SetLaunchVersionAsync(Guid id, string launchVersionId, string? loaderVersion = null, CancellationToken ct = default)
    {
        var current = await GetRequiredAsync(id, ct);
        var updated = current with { LaunchVersionId = launchVersionId, LoaderVersion = loaderVersion ?? current.LoaderVersion };
        await repository.UpsertAsync(updated, ct);
        return updated;
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

    /// <summary>Portable instance configuration (no worlds, no machine-specific Java path).</summary>
    public sealed record InstanceConfig(int Format, string Name, string MinecraftVersion, ModLoaderKind Loader, string? LoaderVersion,
        int MinMemoryMb, int MaxMemoryMb, string ExtraJvmArguments);

    private static readonly System.Text.Json.JsonSerializerOptions ConfigJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task ExportConfigAsync(GameInstance i, string path, CancellationToken ct = default) =>
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(
            new InstanceConfig(1, i.Name, i.MinecraftVersion, i.Loader, i.LoaderVersion, i.MinMemoryMb, i.MaxMemoryMb, i.ExtraJvmArguments), ConfigJson), ct);

    /// <summary>Creates a new instance from an exported config. The caller installs the loader afterwards.</summary>
    public async Task<GameInstance> ImportConfigAsync(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Not an instance configuration file.");
        var c = System.Text.Json.JsonSerializer.Deserialize<InstanceConfig>(await File.ReadAllTextAsync(path, ct), ConfigJson)
            ?? throw new InvalidDataException("Empty instance configuration.");
        if (c.Format != 1) throw new InvalidDataException($"Unsupported instance configuration format {c.Format}.");
        var created = await CreateAsync(c.Name, c.MinecraftVersion, c.Loader, c.LoaderVersion, c.MaxMemoryMb, ct);
        return await UpdateAsync(created with { MinMemoryMb = c.MinMemoryMb, ExtraJvmArguments = c.ExtraJvmArguments }, ct);
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
