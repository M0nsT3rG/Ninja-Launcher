using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Security;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Diagnostics;
using NinjaLauncher.Minecraft.Java.Metadata;
using NinjaLauncher.Mods.Loaders;
using NinjaLauncher.Mods.Modrinth;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

public sealed partial class ModEntry(string fileName, bool enabled, ModUpdate? update) : ObservableObject
{
    public string FileName { get; } = fileName;
    [ObservableProperty] public partial bool Enabled { get; set; } = enabled;
    [ObservableProperty] public partial string? IconUrl { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName))] public partial string? ProjectTitle { get; set; }
    public string DisplayName => ProjectTitle is null ? FileName.Replace(".disabled", "") : $"{ProjectTitle}  ·  {FileName.Replace(".disabled", "")}";
    public ModUpdate? Update { get; } = update;
    public bool HasUpdate => Update is not null;
    public string UpdateText => Update is null ? "" : L.F("Update: {0} → {1}", Update.CurrentVersion ?? "?", Update.Latest.VersionNumber);
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
    private readonly ModCompatibilityChecker _checker;
    private readonly MrpackExporter _exporter;
    private readonly NinjaLauncher.Mods.CurseForge.CurseForgeExporter _curseForgeExporter;
    private readonly NinjaLauncher.Mods.CurseForge.CurseForgeModpackInstaller _curseForgePacks;
    private readonly List<GameInstance> _all = [];
    private IReadOnlyList<ManifestVersion> _manifestVersions = [];

    public LibraryViewModel(UiContext ui, InstanceService instances, VersionCatalog catalog, IEnumerable<ILoaderProvider> loaders,
        GameSessionService game, JobQueue jobs, IUrlLauncher shell, SettingsHolder settings, ModpackInstaller modpacks, ModrinthClient modrinth, ModCompatibilityChecker checker,
        NinjaLauncher.Mods.CurseForge.CurseForgeModpackInstaller curseForgePacks, MrpackExporter exporter,
        NinjaLauncher.Mods.CurseForge.CurseForgeExporter curseForgeExporter)
    {
        (_exporter, _curseForgeExporter) = (exporter, curseForgeExporter);
        (_ui, _instances, _catalog, _game, _jobs, _shell, _settings, _modpacks, _modrinth, _checker) = (ui, instances, catalog, game, jobs, shell, settings, modpacks, modrinth, checker);
        _curseForgePacks = curseForgePacks;
        _loaders = loaders.ToDictionary(l => l.Kind);
        NewMemoryMb = settings.Current.DefaultMaxMemoryMb;
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<string> LoaderVersions { get; } = [];
    public ObservableCollection<ModEntry> Mods { get; } = [];
    public ObservableCollection<string> Worlds { get; } = [];
    public ObservableCollection<string> Backups { get; } = [];
    public ObservableCollection<ModIssue> Issues { get; } = [];
    public ObservableCollection<CrashFile> CrashFiles { get; } = [];
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
    [ObservableProperty] public partial bool CheckingCompatibility { get; set; }
    [ObservableProperty] public partial string CompatibilityStatus { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCrash))] public partial CrashFile? SelectedCrash { get; set; }
    [ObservableProperty] public partial CrashSummary? Crash { get; set; }
    [ObservableProperty] public partial string CrashText { get; set; } = "";
    public bool HasCrash => SelectedCrash is not null;
    public bool HasMissingDependencies => Issues.Any(i => i.MissingProjectId is not null);

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
            _ui.Error(L.F("Could not load Minecraft versions: {0}", e.Message));
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
            if (LoaderVersions.Count == 0) _ui.Error(L.F("{0} has no build for Minecraft {1} that Ninja Launcher can install.", loader, mc));
        });
        if (request == _loaderRequest) LoadingLoaderVersions = false;
    }

    partial void OnSelectedChanged(GameInstance? value)
    {
        _updates = [];
        Issues.Clear();
        CompatibilityStatus = "";
        OnPropertyChanged(nameof(HasMissingDependencies));
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
            if (Selected?.Id == value.Id) DiskUsage = L.F("{0:0.0} MB on disk", bytes / 1024.0 / 1024.0);
        });
    }

    private void RefreshContent(GameInstance instance)
    {
        Mods.Clear(); Worlds.Clear(); Backups.Clear();
        foreach (var (file, enabled) in ModrinthClient.ListMods(_instances.GetGameDirectory(instance)))
            Mods.Add(new ModEntry(file, enabled, _updates.FirstOrDefault(u => u.FileName == file)));
        _ = LoadModIconsAsync(instance, Mods.ToList());
        OnPropertyChanged(nameof(UpdateCount));
        foreach (var w in _instances.ListWorlds(instance)) Worlds.Add(w);
        foreach (var b in _instances.ListBackups(instance)) Backups.Add(b);
        var keep = SelectedCrash?.Path;
        CrashFiles.Clear();
        foreach (var c in CrashReports.List(_instances.GetGameDirectory(instance)).Take(30)) CrashFiles.Add(c);
        SelectedCrash = CrashFiles.FirstOrDefault(c => c.Path == keep) ?? CrashFiles.FirstOrDefault();
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
    private Task ExportModpack() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        _game.EnsureNotRunning(instance, "export it");
        var file = await _ui.SaveFileAsync("Modrinth modpack", $"{SafePath.SanitizeName(instance.Name)}.mrpack");
        if (file is null) return;
        if (!file.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) file += ".mrpack";
        MrpackExportResult? result = null;
        await _jobs.Run($"Export {instance.Name} as .mrpack", async (progress, ct) =>
            result = await _exporter.ExportAsync(instance, _instances.GetGameDirectory(instance), file, "1.0.0",
                new Progress<double>(p => progress.Report(new("Packing", p, 0))), ct));
        _ui.Info(L.F("Exported {0}: {1} file(s) linked from Modrinth, {2} bundled", Path.GetFileName(file), result!.ModrinthFiles, result.OverrideFiles) +
                 (result.SkippedDisabled > 0 ? L.F(", {0} disabled mod(s) left out.", result.SkippedDisabled) : L.T(". Worlds and personal settings are not included.")));
    });

    [RelayCommand]
    private Task ExportCurseForge() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        _game.EnsureNotRunning(instance, "export it");
        var file = await _ui.SaveFileAsync("CurseForge modpack", $"{SafePath.SanitizeName(instance.Name)}-curseforge.zip");
        if (file is null) return;
        if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) file += ".zip";
        NinjaLauncher.Mods.CurseForge.CurseForgeExportResult? result = null;
        await _jobs.Run($"Export {instance.Name} for CurseForge", async (progress, ct) =>
            result = await _curseForgeExporter.ExportAsync(instance, _instances.GetGameDirectory(instance), file, "1.0.0",
                new Progress<double>(p => progress.Report(new("Packing", p, 0))), ct));
        _ui.Info(result!.CurseForgeChecked
            ? L.F("Exported {0}: {1} file(s) linked from CurseForge, {2} bundled.", Path.GetFileName(file), result.LinkedFiles, result.OverrideFiles)
            : L.F("Exported {0} with all {1} file(s) bundled: add a CurseForge API key in Settings so mods can be linked instead.", Path.GetFileName(file), result.OverrideFiles));
    });

    [RelayCommand]
    private Task ChangeIcon() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        var file = await _ui.PickFileAsync("Instance icon", "*.png;*.jpg;*.jpeg;*.webp;*.gif");
        if (file is null) return;
        await _instances.SetIconAsync(instance.Id, file);
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == instance.Id);
    });

    [RelayCommand]
    private Task ResetIcon() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        await _instances.ClearIconAsync(instance.Id);
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == instance.Id);
    });

    [RelayCommand]
    private Task ExportConfig() => _ui.RunAsync(async () =>
    {
        if (Selected is null) return;
        var file = await _ui.SaveFileAsync("Instance configuration", $"{SafePath.SanitizeName(Selected.Name)}.ninja.json");
        if (file is null) return;
        await _instances.ExportConfigAsync(Selected, file);
        _ui.Info(L.F("Exported to {0}.", Path.GetFileName(file)));
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
        _ui.Info(L.F("Imported '{0}'.", instance.Name));
    });

    [RelayCommand]
    private Task ImportModpack() => _ui.RunAsync(async () =>
    {
        var file = await _ui.PickFileAsync("Modpack (.mrpack or CurseForge .zip)", "*.mrpack;*.zip");
        if (file is null) return;
        GameInstance? created = null;
        IReadOnlyList<NinjaLauncher.Mods.CurseForge.BlockedFile> blocked = [];
        await _jobs.Run($"Import {Path.GetFileName(file)}", async (progress, ct) =>
        {
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) (created, blocked) = await _curseForgePacks.InstallFromFileAsync(file, progress, ct);
            else created = await _modpacks.InstallFromFileAsync(file, progress, ct);
        });
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == created!.Id);
        _ui.Info(L.F("Imported '{0}'.", created!.Name));
        await DiscoverViewModel.OfferManualDownloadsAsync(_ui, _shell, blocked, _instances.GetGameDirectory(created));
    });

    [RelayCommand]
    private Task Create() => _ui.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewName)) throw new ArgumentException(L.T("Give the instance a name."));
        if (NewVersion is null) throw new ArgumentException(L.T("Pick a Minecraft version."));
        if (NewLoader != ModLoaderKind.None && NewLoaderVersion is null) throw new ArgumentException(L.F("Pick a {0} version.", NewLoader));
        if (NewLoader == ModLoaderKind.Forge && !NewLoaderVersion!.StartsWith(NewVersion + "-", StringComparison.Ordinal))
            throw new ArgumentException(L.F("Forge {0} is not a build for Minecraft {1}.", NewLoaderVersion, NewVersion));
        var (name, mc, loader, loaderVersion, memory) = (NewName, NewVersion, NewLoader, NewLoaderVersion, NewMemoryMb);
        var instance = await _instances.CreateAsync(name, mc, loader, loaderVersion, memory);
        IsCreating = false;
        if (loader != ModLoaderKind.None)
        {
            _ui.Info(L.F("Installing {0} {1}… see Downloads.", loader, loaderVersion));
            await _jobs.Run($"Install {loader} {loaderVersion} for {name}", async (progress, ct) =>
            {
                var id = await _loaders[loader].InstallAsync(mc, loaderVersion!, progress, ct);
                await _instances.SetLaunchVersionAsync(instance.Id, id, ct: ct);
            });
        }
        await OnShownAsync();
        Selected = Instances.FirstOrDefault(i => i.Id == instance.Id);
        _ui.Info(L.F("Created '{0}'.", name));
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
        _ui.Info(L.T("Instance saved."));
    });

    [RelayCommand]
    private Task Launch() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return; // captured: the selection may change while checking
        if (instance.Loader != ModLoaderKind.None && instance.LaunchVersionId is null)
            throw new InvalidOperationException(L.F("{0} is not installed for this instance yet. Use Repair to install it.", instance.Loader));
        if (!await HomeViewModel.ConfirmModIssuesAsync(_ui, _game, instance)) return;
        _ui.Info(L.F("Preparing {0}… progress is on the Downloads page.", instance.Name));
        await _game.PlayAsync(instance);
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
        _ui.Info(L.F("'{0}' verified and repaired.", instance.Name));
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
        if (!await _ui.ConfirmAsync(L.F("Delete '{0}'? Its worlds, mods and settings in the instance folder will be permanently removed. Backups are kept.", instance.Name))) return;
        await _instances.DeleteAsync(instance.Id);
        Selected = null;
        await OnShownAsync();
        _ui.Info(L.F("Deleted '{0}'.", instance.Name));
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
            ClearIssues();
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
            _ui.Info(_updates.Count == 0 ? L.T("All Modrinth mods are up to date.") : L.F("{0} mod update(s) available.", _updates.Count));
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
                progress.Report(new(L.F("Updating {0}", updates[i].FileName), 100.0 * i / updates.Count, 0));
                if (await _modrinth.ApplyUpdateAsync(updates[i], instance, gameDir, ct) is null) skipped++;
            }
        });
        _updates = [];
        if (Selected?.Id == instance.Id) { RefreshContent(instance); ClearIssues(); }
        _ui.Info(skipped == 0 ? L.T("Mods updated.") : L.F("Mods updated; {0} skipped because they changed since the check.", skipped));
    });

    partial void OnSelectedCrashChanged(CrashFile? value)
    {
        Crash = null;
        CrashText = "";
        if (value is null) return;
        _ = _ui.RunAsync(async () =>
        {
            var text = await Task.Run(() => CrashReports.ReadText(value));
            if (SelectedCrash != value) return;
            CrashText = text;
            Crash = CrashReports.Summarize(text, value.IsJvmCrash);
        });
    }

    [RelayCommand]
    private void OpenCrashFolder()
    {
        if (Selected is null) return;
        var dir = Path.Combine(_instances.GetGameDirectory(Selected), "crash-reports");
        _shell.OpenDirectory(Directory.Exists(dir) ? dir : _instances.GetGameDirectory(Selected));
    }

    private CancellationTokenSource? _iconsCts;

    /// <summary>Identifies installed mods on Modrinth (by hash) to show their logo and name. Cosmetic: failures are ignored.</summary>
    private async Task LoadModIconsAsync(GameInstance instance, IReadOnlyList<ModEntry> entries)
    {
        _iconsCts?.Cancel();
        if (entries.Count == 0) return;
        var cts = _iconsCts = new CancellationTokenSource();
        try
        {
            var dir = Path.Combine(_instances.GetGameDirectory(instance), "mods");
            var hashes = new Dictionary<ModEntry, string>();
            foreach (var e in entries) hashes[e] = await ModrinthClient.Sha1OfAsync(Path.Combine(dir, e.FileName), cts.Token);
            var versions = await _modrinth.IdentifyFilesAsync(hashes.Values.Distinct().ToList(), cts.Token);
            var info = await _modrinth.GetProjectInfoAsync(versions.Values.Select(v => v.ProjectId).Distinct().ToList(), cts.Token);
            if (cts.IsCancellationRequested || Selected?.Id != instance.Id) return;
            foreach (var (entry, hash) in hashes)
                if (versions.TryGetValue(hash, out var v) && info.TryGetValue(v.ProjectId, out var p))
                    (entry.ProjectTitle, entry.IconUrl) = (p.Title, p.IconUrl);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or System.Text.Json.JsonException or TimeoutException) { }
    }

    private int _contentRevision; // bumped on every mod change so in-flight results for older content are dropped

    private void ClearIssues()
    {
        _contentRevision++;
        Issues.Clear();
        CompatibilityStatus = "";
        OnPropertyChanged(nameof(HasMissingDependencies));
    }

    [RelayCommand]
    private Task CheckCompatibility() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        var revision = _contentRevision;
        CheckingCompatibility = true;
        try
        {
            var issues = await _checker.AnalyzeAsync(instance, _instances.GetGameDirectory(instance), CancellationToken.None);
            if (Selected?.Id != instance.Id || revision != _contentRevision) return;
            Issues.Clear();
            foreach (var i in issues) Issues.Add(i);
            var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
            CompatibilityStatus = issues.Count == 0 ? L.T("No problems found.") : L.F("{0} error(s), {1} other note(s).", errors, issues.Count - errors);
            OnPropertyChanged(nameof(HasMissingDependencies));
        }
        finally { CheckingCompatibility = false; }
    });

    [RelayCommand]
    private Task InstallMissingDependencies() => _ui.RunAsync(async () =>
    {
        if (Selected is not { } instance) return;
        _game.EnsureNotRunning(instance, "install mods");
        var gameDir = _instances.GetGameDirectory(instance);
        IReadOnlyList<InstalledContent> added = [];
        await _jobs.Run($"Install missing dependencies for {instance.Name}", async (progress, ct) =>
        {
            // Re-check first: the mods may have changed since the list on screen was produced.
            progress.Report(new(L.T("Checking dependencies"), 5, 0));
            var missing = (await _checker.AnalyzeAsync(instance, gameDir, ct))
                .Where(i => i.MissingProjectId is not null).Select(i => (i.MissingProjectId!, i.MissingVersionId)).ToList();
            progress.Report(new(L.F("Installing {0} dependencies", missing.Count), 20, 0));
            if (missing.Count > 0) added = await _modrinth.InstallProjectsAsync(missing, instance, gameDir, ct);
        });
        _updates = [];
        _ui.Info(added.Count == 0 ? L.T("Nothing to install.") : L.F("Installed {0}.", string.Join(", ", added.Select(a => a.FileName))));
        if (Selected?.Id != instance.Id) return; // the user moved on; don't repaint another instance
        RefreshContent(instance);
        ClearIssues();
        await CheckCompatibility();
    });

    [RelayCommand]
    private Task BackupWorld() => _ui.RunAsync(async () =>
    {
        if (Selected is null || SelectedWorld is null) throw new InvalidOperationException(L.T("Select a world to back up."));
        _game.EnsureNotRunning(Selected, "back up its worlds"); // a running game keeps writing region files
        var file = await _instances.BackupWorldAsync(Selected, SelectedWorld);
        RefreshContent(Selected);
        _ui.Info(L.F("Backup saved: {0}", Path.GetFileName(file)));
    });

    [RelayCommand]
    private Task RestoreBackup() => _ui.RunAsync(async () =>
    {
        if (Selected is null || SelectedBackup is null) throw new InvalidOperationException(L.T("Select a backup to restore."));
        _game.EnsureNotRunning(Selected, "restore a backup");
        await _instances.RestoreBackupAsync(Selected, SelectedBackup);
        RefreshContent(Selected);
        _ui.Info(L.T("Backup restored into the saves folder."));
    });
}
