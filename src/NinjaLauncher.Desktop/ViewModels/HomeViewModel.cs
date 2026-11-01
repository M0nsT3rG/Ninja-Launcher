using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Controls;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Infrastructure.News;
using NinjaLauncher.Minecraft.Bedrock;
using NinjaLauncher.Minecraft.Java.Launch;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

/// <summary>One of the two official Bedrock builds as shown on Home.</summary>
public sealed partial class BedrockEntry(BedrockChannel channel, string title) : ObservableObject
{
    public BedrockChannel Channel { get; } = channel;
    public string Title { get; } = title;
    [ObservableProperty] public partial string Status { get; set; } = L.T("Checking…");
    [ObservableProperty] public partial bool IsInstalled { get; set; }
    [ObservableProperty] public partial bool CanInstall { get; set; }
    public BedrockStatus? Detected { get; set; }
}

public sealed partial class HomeViewModel : ViewModelBase
{
    private const long MaxBackgroundBytes = 16 * 1024 * 1024;

    private readonly UiContext _ui;
    private readonly InstanceService _instances;
    private readonly AccountService _accounts;
    private readonly GameSessionService _game;
    private readonly IActivityLog _activity;
    private readonly SettingsHolder _settings;
    private readonly SkinService _skins;
    private readonly MinecraftNews _news;
    private readonly BedrockProvider _bedrock;
    private readonly ListedServer _listedServer;
    private readonly IUrlLauncher _shell;
    private readonly string _backgroundPath;
    private IReadOnlyList<NewsItem> _allNews = [];
    private bool _loadingAccounts;
    private CancellationTokenSource? _bedrockWatch;

    public HomeViewModel(UiContext ui, InstanceService instances, AccountService accounts, GameSessionService game, IActivityLog activity,
        SettingsHolder settings, SkinService skins, MinecraftNews news, BedrockProvider bedrock, ListedServer listedServer, IUrlLauncher shell, IAppPaths paths)
    {
        (_ui, _instances, _accounts, _game, _activity, _settings) = (ui, instances, accounts, game, activity, settings);
        (_skins, _news, _bedrock, _listedServer, _shell) = (skins, news, bedrock, listedServer, shell);
        _backgroundPath = Path.Combine(paths.DataDirectory, "home-background.img");
        IsJava = settings.Current.LastEdition == GameEdition.Java;
        _game.RunningChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => PlayCommand.NotifyCanExecuteChanged());
        _skins.Changed += id => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (SelectedAccount?.ProfileId == id) _ = _ui.RunAsync(LoadLookAsync);
        });
        LoadBackground();
    }

    public ObservableCollection<GameInstance> Recent { get; } = [];
    public ObservableCollection<MinecraftAccount> Accounts { get; } = [];
    public ObservableCollection<NewsItem> News { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<GameLogLine> GameLog => _game.Log;
    public BedrockEntry[] Bedrock { get; } =
    [
        new(BedrockChannel.Release, L.T("Release")),
        new(BedrockChannel.Preview, L.T("Preview")),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBedrock))] public partial bool IsJava { get; set; }
    public bool IsBedrock => !IsJava;

    [ObservableProperty] public partial MinecraftAccount? SelectedAccount { get; set; }
    [ObservableProperty] public partial string PlayerName { get; set; } = L.T("Not signed in");
    [ObservableProperty] public partial Bitmap? Skin { get; set; }
    [ObservableProperty] public partial Bitmap? Cape { get; set; }
    [ObservableProperty] public partial bool Slim { get; set; }
    [ObservableProperty] public partial bool HasNoRecent { get; set; }
    [ObservableProperty] public partial string NewsStatus { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCustomBackground))] public partial Bitmap? Background { get; set; }
    public bool HasCustomBackground => Background is not null;
    [ObservableProperty] public partial bool IsBackgroundDialogOpen { get; set; }

    public override async Task OnShownAsync()
    {
        _loadingAccounts = true;
        try
        {
            Accounts.Clear();
            foreach (var a in await _accounts.ListAsync()) Accounts.Add(a);
            SelectedAccount = Accounts.FirstOrDefault(a => a.IsActive);
        }
        finally { _loadingAccounts = false; }
        Recent.Clear();
        foreach (var i in (await _instances.ListAsync()).OrderByDescending(i => i.LastPlayedUtc ?? i.CreatedUtc).Take(3)) Recent.Add(i);
        HasNoRecent = Recent.Count == 0;
        Activity.Clear();
        foreach (var a in await _activity.RecentAsync(8)) Activity.Add(a);
        // The figure and the news load in the background; neither should hold up the page.
        _ = _ui.RunAsync(LoadLookAsync);
        _ = LoadNewsAsync();
        if (IsBedrock) await RefreshBedrockAsync();
    }

    partial void OnIsJavaChanged(bool value) => _ = _ui.RunAsync(async () =>
    {
        ShowNews();
        await _settings.UpdateAsync(s => s with { LastEdition = value ? GameEdition.Java : GameEdition.Bedrock });
        if (!value) await RefreshBedrockAsync();
    });

    partial void OnSelectedAccountChanged(MinecraftAccount? value)
    {
        if (_loadingAccounts || value is null || value.IsActive) return;
        _ = _ui.RunAsync(async () =>
        {
            await _accounts.SetActiveAsync(value.ProfileId);
            await OnShownAsync();
        });
    }

    private async Task LoadLookAsync()
    {
        var account = SelectedAccount;
        PlayerName = account?.Username ?? L.T("Not signed in");
        if (account is null) { (Skin, Cape) = (null, null); return; }
        var look = await _skins.GetAsync(account);
        if (SelectedAccount != account) return; // switched while loading
        (Skin, Cape, Slim) = (look?.Skin, look?.ActiveCape, look?.Slim ?? false);
    }

    private async Task LoadNewsAsync()
    {
        NewsStatus = L.T("Loading news…");
        try
        {
            _allNews = await _news.GetAsync();
            ShowNews();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            NewsStatus = L.T("News couldn’t be loaded. Check your connection.");
        }
    }

    private void ShowNews()
    {
        News.Clear();
        foreach (var n in _allNews.Where(n => IsJava ? n.Java : n.Bedrock).Take(12)) News.Add(n);
        NewsStatus = _allNews.Count > 0 && News.Count == 0 ? L.T("No news right now.") : "";
    }

    [RelayCommand]
    private void OpenNews(NewsItem item)
    {
        if (item.ArticleUrl is { } url) _shell.Open(url);
    }

    private bool CanPlay(GameInstance? instance) => instance is not null && !_game.IsRunning(instance.Id);

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task Play(GameInstance? instance) => _ui.RunAsync(async () =>
    {
        if (instance is null) return;
        if (!await ConfirmModIssuesAsync(_ui, _game, instance)) return;
        _ui.Info(L.F("Preparing {0}… progress is on the Downloads page.", instance.Name));
        await _game.PlayAsync(instance);
        await OnShownAsync();
    });

    [RelayCommand]
    private void OpenInstance(GameInstance instance)
    {
        // The Library keeps its selection by id when it reloads its list on showing.
        _ui.Shell.Library.Selected = instance;
        _ui.Navigate(_ui.Shell.Library);
    }

    [RelayCommand] private void ManageInstances() => _ui.Navigate(_ui.Shell.Library);
    [RelayCommand] private void AddMods() => _ui.Navigate(_ui.Shell.Discover);
    [RelayCommand] private void OpenWardrobe() => _ui.Navigate(_ui.Shell.Wardrobe);

    /// <summary>Shared by Home and Library: warns about blocking mod problems and lets the user launch anyway.</summary>
    internal static async Task<bool> ConfirmModIssuesAsync(UiContext ui, GameSessionService game, GameInstance instance)
    {
        var errors = await game.CheckModsBeforeLaunchAsync(instance);
        if (errors.Count == 0) return true;
        var list = string.Join("\n", errors.Take(5).Select(e => "• " + L.T(e.Message))) + (errors.Count > 5 ? "\n• " + L.F("…and {0} more", errors.Count - 5) : "");
        return await ui.ConfirmAsync(L.F("'{0}' has mod problems that will probably stop it from starting:", instance.Name) + "\n\n" + list + "\n\n" + L.T("You can fix them in Library → Check compatibility. Launch anyway?"));
    }

    // ---- Bedrock: only the official builds, opened through Windows; their files are never touched.

    private async Task RefreshBedrockAsync()
    {
        foreach (var entry in Bedrock)
        {
            var status = entry.Detected = await _bedrock.DetectAsync(entry.Channel);
            entry.IsInstalled = status.Support == BedrockSupport.Supported;
            entry.CanInstall = OperatingSystem.IsWindows() && status.Support == BedrockSupport.NotInstalled && status.Version is null;
            entry.Status = entry.IsInstalled
                ? status.Version is { } v ? L.F("Official Bedrock installed · version {0}", v) : L.T("Official Bedrock installed")
                : L.T(status.Message);
        }
    }

    [RelayCommand]
    private Task PlayBedrock(BedrockEntry entry) => _ui.RunAsync(async () =>
    {
        if (entry.Detected is not { Support: BedrockSupport.Supported } status) return;
        _listedServer.EnsureBedrock();
        _bedrock.Launch(status);
        await _activity.AddAsync(entry.Channel == BedrockChannel.Preview ? "Launched Minecraft Preview" : "Launched Minecraft Bedrock Edition");
    });

    // Bedrock is licensed and installed by Microsoft: we open its Store page, then notice when the install lands.
    [RelayCommand(AllowConcurrentExecutions = true)] // pressing again reopens the Store and restarts the watch
    private Task InstallBedrock(BedrockEntry entry) => _ui.RunAsync(async () =>
    {
        if (!_bedrock.OpenStoreApp(entry.Channel)) _shell.Open(BedrockProvider.StoreWebPage(entry.Channel));
        _bedrockWatch?.Cancel();
        var watch = _bedrockWatch = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _ui.Info(L.T("Press Install in the Microsoft Store. Ninja Launcher will notice when it's done."));
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), watch.Token);
                if (!IsBedrock) continue;
                await RefreshBedrockAsync();
                if (entry.IsInstalled) { _ui.Info(L.F("{0} is installed. Press Play.", entry.Title)); return; }
            }
        }
        catch (OperationCanceledException) { } // gave up after 30 minutes, or the button was pressed again
    });

    // ---- Background of the character card

    [RelayCommand] private void EditBackground() => IsBackgroundDialogOpen = true;
    [RelayCommand] private void CloseBackgroundDialog() => IsBackgroundDialogOpen = false;

    [RelayCommand]
    private Task UploadBackground() => _ui.RunAsync(async () =>
    {
        var file = await _ui.PickFileAsync("Home background", "*.png;*.jpg;*.jpeg");
        if (file is null) return;
        var bytes = new FileInfo(file).Length <= MaxBackgroundBytes ? await File.ReadAllBytesAsync(file) : [];
        if (!IsBackgroundImage(bytes)) throw new ArgumentException(L.T("Pick a PNG or JPEG image up to 4096×4096 pixels and 16 MB."));
        var temp = _backgroundPath + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, _backgroundPath, overwrite: true);
        LoadBackground();
        IsBackgroundDialogOpen = false;
    });

    [RelayCommand]
    private void RestoreBackground()
    {
        if (File.Exists(_backgroundPath)) File.Delete(_backgroundPath);
        Background = null;
        IsBackgroundDialogOpen = false;
    }

    private void LoadBackground()
    {
        try
        {
            if (!File.Exists(_backgroundPath)) return;
            var bytes = File.ReadAllBytes(_backgroundPath);
            if (!IsBackgroundImage(bytes)) return;
            using var stream = new MemoryStream(bytes);
            Background = Bitmap.DecodeToWidth(stream, 1600, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { } // keep the default
    }

    /// <summary>PNG or JPEG of sane dimensions; any aspect ratio the card can crop (up to 4:1).</summary>
    private static bool IsBackgroundImage(byte[] bytes) =>
        bytes.Length > 8 && (bytes[0] == 0x89 && bytes[1] == (byte)'P' || bytes[0] == 0xFF && bytes[1] == 0xD8) && RemoteImage.IsSafeImage(bytes);
}
