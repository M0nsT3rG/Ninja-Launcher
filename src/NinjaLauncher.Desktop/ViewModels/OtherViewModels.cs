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

public sealed partial class SettingsViewModel(UiContext ui, SettingsHolder settings, IJavaDiscovery java, IAppPaths paths, IUrlLauncher shell, UpdateService updates, ISecretStore secrets) : ViewModelBase
{
    public ObservableCollection<JavaInstallation> JavaInstallations { get; } = [];
    public IReadOnlyList<ThemeMode> Themes { get; } = Enum.GetValues<ThemeMode>();
    public IReadOnlyList<string> AccentPresets { get; } = ["#B44CFF", "#EC4899", "#8B5CF6", "#D946EF", "#6366F1", "#F472B6"];
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";
    public string DataDirectory => paths.DataDirectory;
    public string Disclaimer => "NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.";

    [ObservableProperty] public partial ThemeMode Theme { get; set; } = settings.Current.Theme;
    [ObservableProperty] public partial string AccentColor { get; set; } = settings.Current.AccentColor;
    [ObservableProperty] public partial int DefaultMemory { get; set; } = settings.Current.DefaultMaxMemoryMb;
    [ObservableProperty] public partial int Concurrency { get; set; } = settings.Current.DownloadConcurrency;
    [ObservableProperty] public partial string InstancesDirectory { get; set; } = settings.Current.CustomInstancesDirectory ?? paths.InstancesDirectory;
    [ObservableProperty] public partial bool Verbose { get; set; } = settings.Current.VerboseLogging;
    [ObservableProperty] public partial string ClientId { get; set; } = settings.Current.MicrosoftClientId;
    [ObservableProperty] public partial bool CheckForUpdates { get; set; } = settings.Current.CheckForUpdates;
    [ObservableProperty] public partial bool DesktopNotifications { get; set; } = settings.Current.DesktopNotifications;
    public IReadOnlyList<string> LanguageNames { get; } = L.Languages.Select(l => l.Name).ToList();
    [ObservableProperty] public partial int LanguageIndex { get; set; } = Math.Max(0, L.Languages.ToList().FindIndex(l => l.Code == settings.Current.Language));
    [ObservableProperty] public partial string CurseForgeKey { get; set; } = "";
    [ObservableProperty] public partial string CurseForgeStatus { get; set; } = "";

    public const string CurseForgeSecretKey = "curseforge-api-key";

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
            ? L.F("A key is saved ({0}). CurseForge is enabled in Discover.", secrets.BackendName)
            : Services.BuiltInKeys.CurseForge is not null
                ? L.T("Using Ninja Launcher's built-in key; CurseForge is enabled in Discover. Save your own key to use it instead.")
                : L.T("No key saved. CurseForge is disabled in Discover.");
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
    }

    partial void OnThemeChanged(ThemeMode value) => ThemeManager.Apply(value, AccentColor);
    partial void OnAccentColorChanged(string value) => ThemeManager.Apply(Theme, value);

    [RelayCommand]
    private void PickAccent(string color) => AccentColor = color;

    [RelayCommand]
    private Task Save() => ui.RunAsync(async () =>
    {
        if (!ThemeManager.IsValidColor(AccentColor)) throw new ArgumentException(L.T("Accent colour must look like #RRGGBB."));
        if (DefaultMemory < 512) throw new ArgumentException(L.T("Default memory must be at least 512 MB."));
        if (ClientId.Trim() is { Length: > 0 } id && !Guid.TryParse(id, out _)) throw new ArgumentException(L.T("The Azure application (client) ID must be a GUID."));
        var customDir = string.IsNullOrWhiteSpace(InstancesDirectory) || Path.GetFullPath(InstancesDirectory) == Path.GetFullPath(Path.Combine(paths.DataDirectory, "instances"))
            ? null : Path.GetFullPath(InstancesDirectory);
        var language = L.Languages[Math.Clamp(LanguageIndex, 0, L.Languages.Count - 1)].Code;
        var restart = customDir != settings.Current.CustomInstancesDirectory || Verbose != settings.Current.VerboseLogging || language != settings.Current.Language;
        await settings.UpdateAsync(current => current with
        {
            Theme = Theme, AccentColor = AccentColor, DefaultMaxMemoryMb = DefaultMemory, DownloadConcurrency = Math.Clamp(Concurrency, 1, 32),
            CustomInstancesDirectory = customDir, VerboseLogging = Verbose, MicrosoftClientId = ClientId.Trim(), CheckForUpdates = CheckForUpdates, Language = language,
            DesktopNotifications = DesktopNotifications,
        });
        ui.Info(restart ? L.T("Settings saved. Restart Ninja Launcher to apply the folder, language and logging changes.") : L.T("Settings saved."));
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
