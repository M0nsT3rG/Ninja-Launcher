using System.Collections.ObjectModel;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.Modrinth;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

public sealed partial class DownloadsViewModel(JobQueue jobs) : ViewModelBase
{
    public ObservableCollection<JobViewModel> Jobs => jobs.Jobs;

    [RelayCommand]
    private void ClearFinished() => jobs.ClearFinished();
}

public sealed partial class AccountsViewModel(UiContext ui, AccountService accounts) : ViewModelBase
{
    public ObservableCollection<MinecraftAccount> Accounts { get; } = [];
    [ObservableProperty] public partial bool IsSigningIn { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool CanUseOffline { get; set; }
    [ObservableProperty] public partial string OfflineName { get; set; } = "";

    public string OfflineLockedText => L.T("Offline profiles unlock after you sign in once with a Microsoft account that owns Minecraft: Java Edition.");
    public string OfflineInfoText => L.T("Offline profiles play singleplayer, LAN and offline-mode servers under any name. Online servers and Realms need your Microsoft account.");

    public bool CanSignIn => accounts.CanSignIn;
    public string SignInUnavailableReason => L.T("Microsoft sign-in needs an Azure application ID approved by Mojang. Enter it under Settings → Accounts. Until then, sign-in is disabled (see docs/research/AUTHENTICATION.md).");
    public string StorageText => accounts.SecretStore.IsAvailable
        ? L.F("Sign-in tokens are stored with {0}.", L.T(accounts.SecretStore.BackendName))
        : L.T("No secure credential storage is available on this system. You will need to sign in again every time the launcher starts.");

    public override async Task OnShownAsync()
    {
        OnPropertyChanged(nameof(CanSignIn));
        CanUseOffline = await accounts.CanUseOfflineAsync();
        Accounts.Clear();
        foreach (var a in await accounts.ListAsync()) Accounts.Add(a);
        Status = Accounts.Count == 0 ? L.T("No accounts yet.") : L.F("{0} account(s).", Accounts.Count);
    }

    [RelayCommand]
    private Task SignIn() => ui.RunAsync(async () =>
    {
        IsSigningIn = true;
        Status = L.T("Complete the sign-in in your browser…");
        try
        {
            var account = await accounts.SignInAsync();
            ui.Info(L.F("Signed in as {0}.", account.Username));
        }
        finally
        {
            IsSigningIn = false;
            await OnShownAsync();
        }
    });

    [RelayCommand]
    private Task AddOffline() => ui.RunAsync(async () =>
    {
        var account = await accounts.AddOfflineAsync(OfflineName);
        OfflineName = "";
        await OnShownAsync();
        ui.Info(L.F("Offline profile {0} added and selected.", account.Username));
    });

    [RelayCommand]
    private Task MakeActive(MinecraftAccount account) => ui.RunAsync(async () =>
    {
        await accounts.SetActiveAsync(account.ProfileId);
        await OnShownAsync();
    });

    [RelayCommand]
    private Task Remove(MinecraftAccount account) => ui.RunAsync(async () =>
    {
        if (!await ui.ConfirmAsync(L.F("Remove {0} from Ninja Launcher? Its stored sign-in is deleted from this computer.", account.Username))) return;
        await accounts.RemoveAsync(account.ProfileId);
        await OnShownAsync();
    });
}

/// <summary>One folder's share of the disk on Settings → Storage.</summary>
public sealed record StorageItem(string Name, string Folder, long Bytes)
{
    public string Size => SettingsViewModel.FormatBytes(Bytes);
}

public sealed partial class SettingsViewModel(UiContext ui, SettingsHolder settings, IJavaDiscovery java, IAppPaths paths, IUrlLauncher shell,
    UpdateService updates, ISecretStore secrets, JobQueue jobs) : ViewModelBase
{
    // Changes save themselves shortly after the last edit; these are the properties that do.
    private static readonly HashSet<string> Saved =
    [
        nameof(Theme), nameof(AccentColor), nameof(DefaultMemory), nameof(Concurrency), nameof(InstancesDirectory), nameof(Verbose), nameof(ClientId),
        nameof(CheckForUpdates), nameof(DesktopNotifications), nameof(LanguageIndex), nameof(OnGameStart), nameof(CustomWindow), nameof(WindowWidth),
        nameof(WindowHeight), nameof(Fullscreen), nameof(DefaultJvmArgs),
    ];

    private readonly LauncherSettings _startup = settings.Current; // what this run started with, for "restart to apply"
    private CancellationTokenSource? _saveDelay;
    private readonly SemaphoreSlim _saveGate = new(1, 1); // saves and resets never interleave
    private bool _loading;

    /// <summary>True while a change waits for its delayed save.</summary>
    public bool HasPendingSave => _saveDelay is { IsCancellationRequested: false };

    /// <summary>Saves a change that is still waiting, e.g. when the window closes.</summary>
    public async Task FlushAsync()
    {
        if (!HasPendingSave) return;
        _saveDelay!.Cancel();
        await SaveAsync();
    }

    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public ObservableCollection<StorageItem> Storage { get; } = [];
    public IReadOnlyList<ThemeMode> Themes { get; } = Enum.GetValues<ThemeMode>();
    public IReadOnlyList<GameStartBehavior> GameStartChoices { get; } = Enum.GetValues<GameStartBehavior>();
    public IReadOnlyList<string> AccentPresets { get; } = ["#B44CFF", "#EC4899", "#8B5CF6", "#D946EF", "#6366F1", "#F472B6"];
    public IReadOnlyList<string> Sections { get; } =
        [L.T("General"), L.T("Game"), L.T("Appearance"), L.T("Storage"), L.T("Accounts and services"), L.T("Advanced"), L.T("About")];
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";
    public string DataDirectory => paths.DataDirectory;
    public string Disclaimer => "NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.";

    /// <summary>This computer's memory, so the slider can't promise more than exists (and leaves room for the system).</summary>
    public int TotalMemoryMb { get; } = (int)Math.Max(2048, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
    public int MaxGameMemoryMb => Math.Max(2048, TotalMemoryMb - 1024);
    public string MemoryText => L.F("{0:N0} MB of the {1:0.#} GB in this computer", DefaultMemory, TotalMemoryMb / 1024.0);

    [ObservableProperty] public partial int SectionIndex { get; set; }
    [ObservableProperty] public partial ThemeMode Theme { get; set; } = settings.Current.Theme;
    [ObservableProperty] public partial string AccentColor { get; set; } = settings.Current.AccentColor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MemoryText))] public partial int DefaultMemory { get; set; } = settings.Current.DefaultMaxMemoryMb;
    [ObservableProperty] public partial int Concurrency { get; set; } = settings.Current.DownloadConcurrency;
    [ObservableProperty] public partial string InstancesDirectory { get; set; } = settings.Current.CustomInstancesDirectory ?? paths.InstancesDirectory;
    [ObservableProperty] public partial bool Verbose { get; set; } = settings.Current.VerboseLogging;
    [ObservableProperty] public partial string ClientId { get; set; } = settings.Current.MicrosoftClientId;
    [ObservableProperty] public partial bool CheckForUpdates { get; set; } = settings.Current.CheckForUpdates;
    [ObservableProperty] public partial bool DesktopNotifications { get; set; } = settings.Current.DesktopNotifications;
    public IReadOnlyList<string> LanguageNames { get; } = L.Languages.Select(l => l.Name).ToList();
    [ObservableProperty] public partial int LanguageIndex { get; set; } = Math.Max(0, L.Languages.ToList().FindIndex(l => l.Code == settings.Current.Language));
    [ObservableProperty] public partial GameStartBehavior OnGameStart { get; set; } = settings.Current.OnGameStart;
    [ObservableProperty] public partial bool CustomWindow { get; set; } = settings.Current.GameWindowWidth is not null;
    [ObservableProperty] public partial int WindowWidth { get; set; } = settings.Current.GameWindowWidth ?? 1280;
    [ObservableProperty] public partial int WindowHeight { get; set; } = settings.Current.GameWindowHeight ?? 720;
    [ObservableProperty] public partial bool Fullscreen { get; set; } = settings.Current.GameFullscreen;
    [ObservableProperty] public partial string DefaultJvmArgs { get; set; } = settings.Current.DefaultJvmArguments;
    [ObservableProperty] public partial string CurseForgeKey { get; set; } = "";
    [ObservableProperty] public partial string CurseForgeStatus { get; set; } = "";
    /// <summary>Why the latest change wasn't saved (shown under the title); empty when everything is saved.</summary>
    [ObservableProperty] public partial string Problem { get; set; } = "";
    [ObservableProperty] public partial bool RestartNeeded { get; set; }
    [ObservableProperty] public partial string StorageTotal { get; set; } = "";
    [ObservableProperty] public partial bool StorageLoading { get; set; }

    public const string CurseForgeSecretKey = "curseforge-api-key";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || !Saved.Contains(e.PropertyName)) return;
        _saveDelay?.Cancel();
        var delay = _saveDelay = new CancellationTokenSource();
        _ = ui.RunAsync(async () =>
        {
            await Task.Delay(400, delay.Token); // typing in a box saves once, not per key
            delay.Cancel(); // no longer pending
            await SaveAsync();
        });
    }

    partial void OnSectionIndexChanged(int value)
    {
        if (Sections[value] == L.T("Storage")) _ = ui.RunAsync(LoadStorageAsync);
    }

    [RelayCommand]
    private Task SaveCurseForgeKey() => ui.RunAsync(async () =>
    {
        var key = CurseForgeKey.Trim();
        if (key.Length < 20 || key.Any(char.IsWhiteSpace)) throw new ArgumentException(L.T("That doesn’t look like a CurseForge API key."));
        await secrets.SetAsync(CurseForgeSecretKey, key);
        CurseForgeKey = "";
        await RefreshCurseForgeStatusAsync();
        ui.Info(L.F("CurseForge API key saved in {0}.", secrets.BackendName));
    });

    [RelayCommand]
    private Task RemoveCurseForgeKey() => ui.RunAsync(async () =>
    {
        await secrets.DeleteAsync(CurseForgeSecretKey);
        await RefreshCurseForgeStatusAsync();
    });

    private async Task RefreshCurseForgeStatusAsync() =>
        CurseForgeStatus = await secrets.GetAsync(CurseForgeSecretKey) is not null
            ? L.F("A key is saved ({0}). CurseForge is enabled in Mods.", secrets.BackendName)
            : Services.BuiltInKeys.CurseForge is not null
                ? L.T("Using Ninja Launcher's built-in key; CurseForge is enabled in Mods. Save your own key to use it instead.")
                : L.T("No key saved. CurseForge is disabled in Mods.");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingUpdate))] public partial string? PendingVersion { get; set; } = updates.PendingVersion;
    [ObservableProperty] public partial string UpdateStatus { get; set; } = updates.UnavailableReason ?? L.T("Updates are signed by the Ninja Launcher release key and only install when you click Install.");
    [ObservableProperty] public partial bool IsUpdating { get; set; }
    public bool UpdatesAvailable => updates.IsAvailable;
    public bool HasPendingUpdate => PendingVersion is not null;

    [RelayCommand]
    private Task CheckUpdates() => ui.RunAsync(async () =>
    {
        UpdateStatus = L.T("Checking for updates…");
        PendingVersion = await updates.CheckAsync();
        UpdateStatus = PendingVersion is null ? L.F("Ninja Launcher {0} is up to date.", Version) : L.F("Version {0} is available.", PendingVersion);
    });

    [RelayCommand]
    private Task InstallUpdate() => ui.RunAsync(async () =>
    {
        if (!await ui.ConfirmAsync(L.F("Download Ninja Launcher {0} and restart now? Running games are not affected, but the launcher will close.", PendingVersion))) return;
        IsUpdating = true;
        try { await updates.DownloadAndRestartAsync(p => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateStatus = L.F("Downloading update… {0}%", p))); }
        finally { IsUpdating = false; }
    });

    public override async Task OnShownAsync()
    {
        await RefreshCurseForgeStatusAsync();
        JavaInstallations.Clear();
        foreach (var j in await java.DiscoverAsync()) JavaInstallations.Add(j);
        if (Sections[SectionIndex] == L.T("Storage")) await LoadStorageAsync();
    }

    partial void OnThemeChanged(ThemeMode value) => ThemeManager.Apply(value, AccentColor);
    partial void OnAccentColorChanged(string value) => ThemeManager.Apply(Theme, value);

    [RelayCommand]
    private void PickAccent(string color) => AccentColor = color;

    /// <summary>Saves everything that is valid. An invalid entry is reported once, under the title, and not saved.</summary>
    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var accentOk = ThemeManager.IsValidColor(AccentColor);
            var memoryOk = DefaultMemory >= 512;
            var clientIdOk = ClientId.Trim() is not { Length: > 0 } id || Guid.TryParse(id, out _);
            var windowOk = !CustomWindow || (WindowWidth is >= 320 and <= 7680 && WindowHeight is >= 240 and <= 4320);
            Problem = !accentOk ? L.T("Accent colour must look like #RRGGBB.")
                : !memoryOk ? L.T("Default memory must be at least 512 MB.")
                : !clientIdOk ? L.T("The Azure application (client) ID must be a GUID.")
                : !windowOk ? L.T("The game window must be between 320×240 and 7680×4320.")
                : "";
            string? customDir;
            try
            {
                customDir = string.IsNullOrWhiteSpace(InstancesDirectory) || Path.GetFullPath(InstancesDirectory) == Path.GetFullPath(Path.Combine(paths.DataDirectory, "instances"))
                    ? null : Path.GetFullPath(InstancesDirectory);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                customDir = settings.Current.CustomInstancesDirectory; // not a valid path yet: keep the saved folder
                Problem = Problem.Length > 0 ? Problem : L.T("The profiles folder isn’t a valid path.");
            }
            var language = L.Languages[Math.Clamp(LanguageIndex, 0, L.Languages.Count - 1)].Code;
            await settings.UpdateAsync(current => current with
            {
                Theme = Theme, AccentColor = accentOk ? AccentColor : current.AccentColor, DefaultMaxMemoryMb = memoryOk ? DefaultMemory : current.DefaultMaxMemoryMb,
                DownloadConcurrency = Math.Clamp(Concurrency, 1, 32), CustomInstancesDirectory = customDir, VerboseLogging = Verbose,
                MicrosoftClientId = clientIdOk ? ClientId.Trim() : current.MicrosoftClientId, CheckForUpdates = CheckForUpdates, Language = language,
                DesktopNotifications = DesktopNotifications, OnGameStart = OnGameStart, GameFullscreen = Fullscreen, DefaultJvmArguments = DefaultJvmArgs.Trim(),
                GameWindowWidth = windowOk ? (CustomWindow ? WindowWidth : null) : current.GameWindowWidth,
                GameWindowHeight = windowOk ? (CustomWindow ? WindowHeight : null) : current.GameWindowHeight,
            });
            RefreshRestartNeeded();
        }
        finally { _saveGate.Release(); }
    }

    // 6: compares what is saved now with what this run started with.
    private void RefreshRestartNeeded()
    {
        var now = settings.Current;
        RestartNeeded = now.CustomInstancesDirectory != _startup.CustomInstancesDirectory || now.VerboseLogging != _startup.VerboseLogging || now.Language != _startup.Language;
    }

    // ---- Storage

    private async Task LoadStorageAsync()
    {
        StorageLoading = true;
        try
        {
            (string Name, string Folder)[] folders =
            [
                (L.T("Game files (versions, libraries, assets, Java)"), paths.GameDirectory), (L.T("Profiles"), InstancesDirectory),
                (L.T("World backups"), paths.BackupsDirectory), (L.T("Cache"), paths.CacheDirectory), (L.T("Logs"), paths.LogsDirectory),
            ];
            var items = await Task.Run(() => folders.Select(f => new StorageItem(f.Name, f.Folder, FolderSize(f.Folder))).ToList());
            Storage.Clear();
            foreach (var i in items) Storage.Add(i);
            StorageTotal = L.F("{0} in total", FormatBytes(items.Sum(i => i.Bytes)));
        }
        finally { StorageLoading = false; }
    }

    internal static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            try { total += f.Length; } catch (IOException) { } // vanished while counting
        return total;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => L.F("{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => L.F("{0:0.0} MB", bytes / (double)(1L << 20)),
        _ => L.F("{0:0} KB", bytes / 1024.0),
    };

    [RelayCommand]
    private void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        shell.OpenDirectory(folder);
    }

    /// <summary>The cache only holds things that are fetched again when needed: logos, the version list, modpack downloads, installer scratch space.</summary>
    [RelayCommand]
    private Task ClearCache() => ui.RunAsync(async () =>
    {
        bool Busy() => jobs.Jobs.Any(j => j.IsRunning);
        if (Busy()) throw new InvalidOperationException(L.T("Wait for the downloads on the Downloads page to finish, then clear the cache."));
        if (!await ui.ConfirmAsync(L.T("Clear the cache? Logos, the version list and leftover downloads are fetched again when needed. Profiles, worlds and settings are not touched."))) return;
        if (Busy()) throw new InvalidOperationException(L.T("Wait for the downloads on the Downloads page to finish, then clear the cache."));
        // shortcut: a job started while one entry is being deleted can still lose that entry's files (it then fails and can be retried);
        // a shared lock with the installers would close that gap if it ever matters.
        var freed = await Task.Run(() =>
        {
            long done = 0;
            if (!Directory.Exists(paths.CacheDirectory)) return done;
            foreach (var entry in new DirectoryInfo(paths.CacheDirectory).EnumerateFileSystemInfos())
            {
                if (Busy()) break; // something started: stop and leave the rest
                try
                {
                    var size = entry is DirectoryInfo dir ? FolderSize(dir.FullName) : ((FileInfo)entry).Length;
                    if (entry is DirectoryInfo d) d.Delete(recursive: true); else entry.Delete();
                    done += size;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // in use: left for next time
            }
            return done;
        });
        await LoadStorageAsync();
        ui.Info(L.F("Freed {0}.", FormatBytes(freed)));
    });

    /// <summary>Back to the defaults, except what would lose things: the profiles folder and a custom app ID stay.</summary>
    [RelayCommand]
    private Task ResetDefaults() => ui.RunAsync(async () =>
    {
        if (!await ui.ConfirmAsync(L.T("Put every setting back to its default? Your profiles, accounts, profiles folder and CurseForge key are kept."))) return;
        _saveDelay?.Cancel();
        await _saveGate.WaitAsync();
        try { await settings.UpdateAsync(c => new LauncherSettings { CustomInstancesDirectory = c.CustomInstancesDirectory, MicrosoftClientId = c.MicrosoftClientId, LastEdition = c.LastEdition }); }
        finally { _saveGate.Release(); }
        var d = settings.Current;
        _loading = true;
        try
        {
            (Theme, AccentColor, DefaultMemory, Concurrency, Verbose, CheckForUpdates, DesktopNotifications) =
                (d.Theme, d.AccentColor, d.DefaultMaxMemoryMb, d.DownloadConcurrency, d.VerboseLogging, d.CheckForUpdates, d.DesktopNotifications);
            (OnGameStart, CustomWindow, WindowWidth, WindowHeight, Fullscreen, DefaultJvmArgs) = (d.OnGameStart, false, 1280, 720, d.GameFullscreen, d.DefaultJvmArguments);
            LanguageIndex = Math.Max(0, L.Languages.ToList().FindIndex(l => l.Code == d.Language));
        }
        finally { _loading = false; }
        RefreshRestartNeeded();
        ui.Info(L.T("Settings are back to their defaults."));
    });

    [RelayCommand]
    private void OpenLogs() => shell.OpenDirectory(paths.LogsDirectory);

    [RelayCommand]
    private void OpenData() => shell.OpenDirectory(paths.DataDirectory);

    [RelayCommand]
    private Task Rescan() => ui.RunAsync(OnShownAsync);

    [RelayCommand]
    private void OpenLink(string url) => shell.Open(new Uri(url));
}

public static class AccountsDisplay
{
    public static readonly Avalonia.Data.Converters.IValueConverter Initial =
        new Avalonia.Data.Converters.FuncValueConverter<string, string>(s => string.IsNullOrEmpty(s) ? "?" : s[..1].ToUpperInvariant());
}

/// <summary>Applies light/dark/system theme and the accent colour to the running application.</summary>
public static class ThemeManager
{
    public static bool IsValidColor(string hex) => Avalonia.Media.Color.TryParse(hex, out _);

    public static void Apply(ThemeMode mode, string accent)
    {
        var app = Avalonia.Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = mode switch { ThemeMode.Light => ThemeVariant.Light, ThemeMode.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
        if (Avalonia.Media.Color.TryParse(accent, out var color))
        {
            app.Resources["NinjaAccentColor"] = color;
            app.Resources["NinjaAccentBrush"] = new Avalonia.Media.SolidColorBrush(color);
            app.Resources["SystemAccentColor"] = color;
        }
    }
}
