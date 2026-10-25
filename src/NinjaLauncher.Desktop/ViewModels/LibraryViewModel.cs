using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Metadata;
using NinjaLauncher.Mods.Loaders;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.Desktop.ViewModels;

public sealed partial class ModEntry(string fileName, bool enabled, ModUpdate? update) : ObservableObject
{
    public string FileName { get; } = fileName;
    [ObservableProperty] public partial bool Enabled { get; set; } = enabled;
    public string DisplayName => FileName.Replace(".disabled", "");
    public ModUpdate? Update { get; } = update;
    public bool HasUpdate => Update is not null;
    public string UpdateText => Update is null ? "" : $"Update: {Update.CurrentVersion ?? "?"} → {Update.Latest.VersionNumber}";
}

public sealed partial class LibraryViewModel : ViewModelBase
{
    private readonly UiContext _ui;
    private readonly InstanceService _instances;
    private readonly VersionCatalog _catalog;
    private readonly IReadOnlyDictionary<ModLoaderKind, ILoaderProvider> _loaders;
    private readonly GameSessionService _game;
    private readonly JobQueue _jobs;
    private readonly IUrlLauncher _shell;
    private readonly SettingsHolder _settings;
    private readonly ModpackInstaller _modpacks;
    private readonly ModrinthClient _modrinth;
    private readonly List<GameInstance> _all = [];
    private IReadOnlyList<ManifestVersion> _manifestVersions = [];

    public LibraryViewModel(UiContext ui, InstanceService instances, VersionCatalog catalog, IEnumerable<ILoaderProvider> loaders,
        GameSessionService game, JobQueue jobs, IUrlLauncher shell, SettingsHolder settings, ModpackInstaller modpacks, ModrinthClient modrinth)
    {
        (_ui, _instances, _catalog, _game, _jobs, _shell, _settings, _modpacks, _modrinth) = (ui, instances, catalog, game, jobs, shell, settings, modpacks, modrinth);
        _loaders = loaders.ToDictionary(l => l.Kind);
        NewMemoryMb = settings.Current.DefaultMaxMemoryMb;
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<string> LoaderVersions { get; } = [];
    public ObservableCollection<ModEntry> Mods { get; } = [];
    public ObservableCollection<string> Worlds { get; } = [];
    public ObservableCollection<string> Backups { get; } = [];
    public IReadOnlyList<ModLoaderKind> LoaderKinds { get; } = Enum.GetValues<ModLoaderKind>();

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool ShowSnapshots { get; set; }
    [ObservableProperty] public partial bool IsCreating { get; set; }
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string? NewVersion { get; set; }
    [ObservableProperty] public partial ModLoaderKind NewLoader { get; set; }
    [ObservableProperty] public partial string? NewLoaderVersion { get; set; }
    [ObservableProperty] public partial int NewMemoryMb { get; set; }
    [ObservableProperty] public partial bool LoadingLoaderVersions { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial GameInstance? Selected { get; set; }
    public bool HasSelection => Selected is not null;

    // Editable copy of the selected instance.
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial int EditMinMemory { get; set; }
    [ObservableProperty] public partial int EditMaxMemory { get; set; }
    [ObservableProperty] public partial string EditJvmArgs { get; set; } = "";
    [ObservableProperty] public partial string EditJavaPath { get; set; } = "";
    [ObservableProperty] public partial string DiskUsage { get; set; } = "";
    [ObservableProperty] public partial string? SelectedWorld { get; set; }
    [ObservableProperty] public partial string? SelectedBackup { get; set; }
    [ObservableProperty] public partial bool CheckingUpdates { get; set; }

    private IReadOnlyList<ModUpdate> _updates = [];
    public int UpdateCount => _updates.Count;

    public override async Task OnShownAsync()
    {
        _all.Clear();
        _all.AddRange(await _instances.ListAsync());
        ApplyFilter();
        if (Versions.Count == 0) await LoadVersionsAsync();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnShowSnapshotsChanged(bool value) => FillVersions();

    private void ApplyFilter()
    {
        var keep = Selected?.Id;
        Instances.Clear();
        foreach (var i in _all.Where(i => string.IsNullOrWhiteSpace(Search) || i.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                     || i.MinecraftVersion.Contains(Search, StringComparison.OrdinalIgnoreCase) || i.Loader.ToString().Contains(Search, StringComparison.OrdinalIgnoreCase)))
            Instances.Add(i);
        Selected = Instances.FirstOrDefault(i => i.Id == keep);
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            _manifestVersions = (await _catalog.GetManifestAsync()).Versions;
            FillVersions();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _ui.Error($"Could not load Minecraft versions: {e.Message}");
        }
    }

    private void FillVersions()
    {
        Versions.Clear();
        foreach (var v in _manifestVersions.Where(v => ShowSnapshots ? v.Type is "release" or "snapshot" : v.Type == "release")) Versions.Add(v.Id);
        NewVersion ??= Versions.FirstOrDefault();
    }

    partial void OnNewVersionChanged(string? value) => _ = RefreshLoaderVersionsAsync();
    partial void OnNewLoaderChanged(ModLoaderKind value) => _ = RefreshLoaderVersionsAsync();

    private int _loaderRequest;

    private async Task RefreshLoaderVersionsAsync()
    {
        var request = ++_loaderRequest; // results of an older (slower) request are discarded
        var (loader, mc) = (NewLoader, NewVersion);
        LoaderVersions.Clear();
        NewLoaderVersion = null;
        if (loader == ModLoaderKind.None || mc is null) { LoadingLoaderVersions = false; return; }
        LoadingLoaderVersions = true;
        await _ui.RunAsync(async () =>
        {
            var versions = await _loaders[loader].GetVersionsAsync(mc, CancellationToken.None);
            if (request != _loaderRequest) return;
            foreach (var v in versions.OrderByDescending(v => v.Stable).Take(60)) LoaderVersions.Add(v.Version);
            NewLoaderVersion = LoaderVersions.FirstOrDefault();
            if (LoaderVersions.Count == 0) _ui.Error($"{loader} has no build for Minecraft {mc} that Ninja Launcher can install.");
        });
        if (request == _loaderRequest) LoadingLoaderVersions = false;
    }

    partial void OnSelectedChanged(GameInstance? value)
    {
        _updates = [];
        Mods.Clear(); Worlds.Clear(); Backups.Clear();
        DiskUsage = "";
        if (value is null) return;
        EditName = value.Name;
        EditMinMemory = value.MinMemoryMb;
        EditMaxMemory = value.MaxMemoryMb;
        EditJvmArgs = value.ExtraJvmArguments;
        EditJavaPath = value.JavaPath ?? "";
        _ = _ui.RunAsync(async () =>
        {
            RefreshContent(value);
            var bytes = await _instances.GetDiskUsageAsync(value);
            if (Selected?.Id == value.Id) DiskUsage = $"{bytes / 1024.0 / 1024.0:0.0} MB on disk";
        });
    }

    private void RefreshContent(GameInstance instance)
    {
        Mods.Clear(); Worlds.Clear(); Backups.Clear();
        foreach (var (file, enabled) in ModrinthClient.ListMods(_instances.GetGameDirectory(instance)))
            Mods.Add(new ModEntry(file, enabled, _updates.FirstOrDefault(u => u.FileName == file)));
        OnPropertyChanged(nameof(UpdateCount));
        foreach (var w in _instances.ListWorlds(instance)) Worlds.Add(w);
        foreach (var b in _instances.ListBackups(instance)) Backups.Add(b);
    }

    [RelayCommand]
    private void StartCreate()
    {
        IsCreating = true;
        NewName = "";
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    [RelayCommand]
    private Task ExportConfig() => _ui.RunAsync(async () =>
    {
        if (Selected is null) return;
        var file = await _ui.SaveFileAsync("Instance configuration", $"{SafePath.SanitizeName(Selected.Name)}.ninja.json");
        if (file is null) return;
        await _instances.ExportConfigAsync(Selected, file);
        _ui.Info($"Exported to {Path.GetFileName(file)}.");
    });

    [RelayCommand]
    private Task ImportConfig() => _ui.RunAsync(async () =>
    {
        var file = await _ui.PickFileAsync("Instance configuration", "*.json");
        if (file is null) return;
        var instance = await _instances.ImportConfigAsync(file);
        if (instance.Loader != ModLoaderKind.None && instance.LoaderVersion is { } lv)
            await _jobs.Run($"Install {instance.Loader} {lv} for {instance.Name}", async (progress, ct) =>
            {
                var id = await _loaders[instance.Loader].InstallAsync(instance.MinecraftVersion, lv, progress, ct);
                await _instances.SetLaunchVersionAsync(instance.Id, id, ct: ct);
            });
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == instance.Id);
        _ui.Info($"Imported '{instance.Name}'.");
    });

    [RelayCommand]
    private Task ImportModpack() => _ui.RunAsync(async () =>
    {
        var file = await _ui.PickFileAsync("Modrinth modpack", "*.mrpack");
        if (file is null) return;
        GameInstance? created = null;
        await _jobs.Run($"Import {Path.GetFileName(file)}", async (progress, ct) => created = await _modpacks.InstallFromFileAsync(file, progress, ct));
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == created!.Id);
        _ui.Info($"Imported '{created!.Name}'.");
    });

    [RelayCommand]
    private Task Create() => _ui.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewName)) throw new ArgumentException("Give the instance a name.");
        if (NewVersion is null) throw new ArgumentException("Pick a Minecraft version.");
        if (NewLoader != ModLoaderKind.None && NewLoaderVersion is null) throw new ArgumentException($"Pick a {NewLoader} version.");
        if (NewLoader == ModLoaderKind.Forge && !NewLoaderVersion!.StartsWith(NewVersion + "-", StringComparison.Ordinal))
            throw new ArgumentException($"Forge {NewLoaderVersion} is not a build for Minecraft {NewVersion}.");
        var (name, mc, loader, loaderVersion, memory) = (NewName, NewVersion, NewLoader, NewLoaderVersion, NewMemoryMb);
        var instance = await _instances.CreateAsync(name, mc, loader, loaderVersion, memory);
        IsCreating = false;
        if (loader != ModLoaderKind.None)
        {
            _ui.Info($"Installing {loader} {loaderVersion}… see Downloads.");
            await _jobs.Run($"Install {loader} {loaderVersion} for {name}", async (progress, ct) =>
            {
                var id = await _loaders[loader].InstallAsync(mc, loaderVersion!, progress, ct);
                await _instances.SetLaunchVersionAsync(instance.Id, id, ct: ct);
            });
        }
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == instance.Id);
        _ui.Info($"Created '{name}'.");
    });

    [RelayCommand]
    private Task Save() => _ui.RunAsync(async () =>
    {
        if (Selected is null) return;
        _updates = []; // version or loader may change what is compatible
        var updated = await _instances.UpdateAsync(Selected with
        {
            Name = EditName, MinMemoryMb = EditMinMemory, MaxMemoryMb = EditMaxMemory,
            ExtraJvmArguments = EditJvmArgs.Trim(), JavaPath = string.IsNullOrWhiteSpace(EditJavaPath) ? null : EditJavaPath.Trim(),
        });
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == updated.Id);
        _ui.Info("Instance saved.");
    });

    [RelayCommand]
    private Task Launch() => _ui.RunAsync(async () =>
    {
        if (Selected is null) return;
        if (Selected.Loader != ModLoaderKind.None && Selected.LaunchVersionId is null)
            throw new InvalidOperationException($"{Selected.Loader} is not installed for this instance yet. Use Repair to install it.");
        _ui.Info($"Preparing {Selected.Name}… progress is on the Downloads page.");
        await _game.PlayAsync(Selected);
    });

    [RelayCommand]
    private Task Repair() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        _game.EnsureNotRunning(instance, "repair it");
        if (instance.Loader != ModLoaderKind.None && instance.LoaderVersion is { } lv)
        {
            await _jobs.Run($"Reinstall {instance.Loader} for {instance.Name}", async (progress, ct) =>
            {
                var id = await _loaders[instance.Loader].InstallAsync(instance.MinecraftVersion, lv, progress, ct);
                instance = await _instances.SetLaunchVersionAsync(instance.Id, id, ct: ct);
            });
        }
        await _game.RepairAsync(instance);
        _ui.Info($"'{instance.Name}' verified and repaired.");
        await OnShownAsync();
    });

    [RelayCommand]
    private Task Clone() => _ui.RunAsync(async () =>
    {
        if (Selected is null) return;
        var clone = await _instances.CloneAsync(Selected.Id, $"{Selected.Name} (copy)"[..Math.Min(64, Selected.Name.Length + 7)]);
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == clone.Id);
    });

    [RelayCommand]
    private Task Delete() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        _game.EnsureNotRunning(instance, "delete it");
        if (!await _ui.ConfirmAsync($"Delete '{instance.Name}'? Its worlds, mods and settings in the instance folder will be permanently removed. Backups are kept.")) return;
        await _instances.DeleteAsync(instance.Id);
        Selected = null;
        await OnShownAsync();
        _ui.Info($"Deleted '{instance.Name}'.");
    });

    [RelayCommand]
    private void OpenFolder()
    {
        if (Selected is null) return;
        var dir = _instances.GetGameDirectory(Selected);
        Directory.CreateDirectory(dir);
        _shell.OpenDirectory(dir);
    }

    [RelayCommand]
    private void ToggleMod(ModEntry mod)
    {
        if (Selected is null) return;
        try
        {
            ModrinthClient.SetModEnabled(_instances.GetGameDirectory(Selected), mod.FileName, !mod.Enabled);
            _updates = []; // file names changed; re-check before updating
            RefreshContent(Selected);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ui.Error(e.Message); }
    }

    [RelayCommand]
    private Task CheckModUpdates() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        CheckingUpdates = true;
        try
        {
            _updates = await _modrinth.CheckUpdatesAsync(instance, _instances.GetGameDirectory(instance), CancellationToken.None);
            if (Selected?.Id != instance.Id) return;
            RefreshContent(instance);
            _ui.Info(_updates.Count == 0 ? "All Modrinth mods are up to date." : $"{_updates.Count} mod update(s) available.");
        }
        finally { CheckingUpdates = false; }
    });

    [RelayCommand]
    private Task UpdateMods() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance || _updates.Count == 0) return;
        _game.EnsureNotRunning(instance, "update its mods");
        var updates = _updates;
        var gameDir = _instances.GetGameDirectory(instance);
        var skipped = 0;
        await _jobs.Run($"Update {updates.Count} mod(s) in {instance.Name}", async (progress, ct) =>
        {
            for (var i = 0; i < updates.Count; i++)
            {
                progress.Report(new($"Updating {updates[i].FileName}", 100.0 * i / updates.Count, 0));
                if (await _modrinth.ApplyUpdateAsync(updates[i], instance, gameDir, ct) is null) skipped++;
            }
        });
        _updates = [];
        if (Selected?.Id == instance.Id) RefreshContent(instance);
        _ui.Info(skipped == 0 ? "Mods updated." : $"Mods updated; {skipped} skipped because they changed since the check.");
    });

    [RelayCommand]
    private Task BackupWorld() => _ui.RunAsync(async () =>
    {
        if (Selected is null || SelectedWorld is null) throw new InvalidOperationException("Select a world to back up.");
        _game.EnsureNotRunning(Selected, "back up its worlds"); // a running game keeps writing region files
        var file = await _instances.BackupWorldAsync(Selected, SelectedWorld);
        RefreshContent(Selected);
        _ui.Info($"Backup saved: {Path.GetFileName(file)}");
    });

    [RelayCommand]
    private Task RestoreBackup() => _ui.RunAsync(async () =>
    {
        if (Selected is null || SelectedBackup is null) throw new InvalidOperationException("Select a backup to restore.");
        _game.EnsureNotRunning(Selected, "restore a backup");
        await _instances.RestoreBackupAsync(Selected, SelectedBackup);
        RefreshContent(Selected);
        _ui.Info("Backup restored into the saves folder.");
    });
}
