using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Bedrock;
using NinjaLauncher.Minecraft.Java.Launch;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly UiContext _ui;
    private readonly InstanceService _instances;
    private readonly AccountService _accounts;
    private readonly GameSessionService _game;
    private readonly BedrockProvider _bedrock;
    private readonly IActivityLog _activity;
    private readonly SettingsHolder _settings;
    private readonly IUrlLauncher _shell;
    private readonly ListedServer _listedServer;
    private BedrockStatus? _bedrockStatus;

    public HomeViewModel(UiContext ui, InstanceService instances, AccountService accounts, GameSessionService game,
        BedrockProvider bedrock, IActivityLog activity, SettingsHolder settings, IUrlLauncher shell, ListedServer listedServer)
    {
        (_ui, _instances, _accounts, _game, _bedrock, _activity, _settings, _shell, _listedServer) = (ui, instances, accounts, game, bedrock, activity, settings, shell, listedServer);
        IsJava = settings.Current.LastEdition == GameEdition.Java;
        _game.RunningChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => PlayCommand.NotifyCanExecuteChanged());
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<GameLogLine> GameLog => _game.Log;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBedrock)), NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool IsJava { get; set; }
    public bool IsBedrock => !IsJava;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedSummary)), NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial GameInstance? SelectedInstance { get; set; }

    [ObservableProperty] public partial string AccountText { get; set; } = L.T("Not signed in");
    [ObservableProperty] public partial string BedrockText { get; set; } = "";
    [ObservableProperty] public partial bool HasBedrockData { get; set; }
    [ObservableProperty] public partial bool CanGetBedrock { get; set; }

    /// <summary>Microsoft's official store page for Minecraft for Windows (product 9NBLGGH2JHXJ).</summary>
    public const string BedrockStoreUrl = "https://www.xbox.com/games/store/minecraft-for-windows/9nblggh2jhxj";

    public string SelectedSummary => SelectedInstance is { } i
        ? $"Minecraft {i.MinecraftVersion}{(i.Loader == ModLoaderKind.None ? "" : $" · {i.Loader} {i.LoaderVersion}")} · {i.MaxMemoryMb} MB" +
          (i.LastPlayedUtc is { } t ? L.F(" · last played {0:g}", t.LocalDateTime) : "")
        : L.T("Create an instance in the Library to start playing.");

    public override async Task OnShownAsync()
    {
        var active = await _accounts.GetActiveAsync();
        AccountText = active is null ? L.T("Not signed in")
            : active.Kind == AccountKind.Offline ? L.F("Offline as {0}", active.Username) : L.F("Signed in as {0}", active.Username);
        var list = await _instances.ListAsync();
        var selectedId = SelectedInstance?.Id ?? _settings.Current.SelectedInstanceId;
        Instances.Clear();
        foreach (var i in list.OrderByDescending(i => i.LastPlayedUtc ?? i.CreatedUtc)) Instances.Add(i);
        SelectedInstance = Instances.FirstOrDefault(i => i.Id == selectedId) ?? Instances.FirstOrDefault();
        Activity.Clear();
        foreach (var a in await _activity.RecentAsync(8)) Activity.Add(a);
        if (IsBedrock) await RefreshBedrockAsync();
    }

    partial void OnIsJavaChanged(bool value) => _ = _ui.RunAsync(async () =>
    {
        await _settings.UpdateAsync(s => s with { LastEdition = value ? GameEdition.Java : GameEdition.Bedrock });
        if (!value) await RefreshBedrockAsync();
    });

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        if (value is not null && value.Id != _settings.Current.SelectedInstanceId)
            _ = _ui.RunAsync(() => _settings.UpdateAsync(s => s with { SelectedInstanceId = value.Id }));
    }

    private async Task RefreshBedrockAsync()
    {
        BedrockText = L.T("Checking for Bedrock Edition…");
        _bedrockStatus = await _bedrock.DetectAsync();
        BedrockText = L.T(_bedrockStatus.Message) + (_bedrockStatus.Version is { } v ? L.F(" (version {0})", v) : "");
        HasBedrockData = _bedrockStatus.DataDirectory is not null;
        CanGetBedrock = OperatingSystem.IsWindows() && _bedrockStatus.Support == BedrockSupport.NotInstalled && _bedrockStatus.Version is null;
        PlayCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlay() => IsJava
        ? SelectedInstance is { } i && !_game.IsRunning(i.Id)
        : _bedrockStatus?.Support == BedrockSupport.Supported;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task Play() => _ui.RunAsync(async () =>
    {
        if (IsBedrock)
        {
            _listedServer.EnsureBedrock();
            _bedrock.Launch(_bedrockStatus!);
            await _activity.AddAsync("Launched Minecraft Bedrock Edition");
            return;
        }
        if (SelectedInstance is not { } instance) return; // captured: the selection may change while checking
        if (!await ConfirmModIssuesAsync(_ui, _game, instance)) return;
        _ui.Info(L.F("Preparing {0}… progress is on the Downloads page.", instance.Name));
        await _game.PlayAsync(instance);
        await OnShownAsync();
    });

    /// <summary>Shared by Home and Library: warns about blocking mod problems and lets the user launch anyway.</summary>
    internal static async Task<bool> ConfirmModIssuesAsync(UiContext ui, GameSessionService game, GameInstance instance)
    {
        var errors = await game.CheckModsBeforeLaunchAsync(instance);
        if (errors.Count == 0) return true;
        var list = string.Join("\n", errors.Take(5).Select(e => "• " + L.T(e.Message))) + (errors.Count > 5 ? "\n• " + L.F("…and {0} more", errors.Count - 5) : "");
        return await ui.ConfirmAsync(L.F("'{0}' has mod problems that will probably stop it from starting:", instance.Name) + "\n\n" + list + "\n\n" + L.T("You can fix them in Library → Check compatibility. Launch anyway?"));
    }

    private CancellationTokenSource? _bedrockWatch;

    // Bedrock is licensed and installed by Microsoft: we open its Store page, then notice when the install lands.
    [RelayCommand(AllowConcurrentExecutions = true)] // pressing again reopens the Store and restarts the watch
    private Task GetBedrock() => _ui.RunAsync(async () =>
    {
        if (!_bedrock.OpenStoreApp()) _shell.Open(new Uri(BedrockStoreUrl));
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
                if (_bedrockStatus?.Support == BedrockSupport.Supported) { _ui.Info(L.T("Minecraft for Windows is installed. Press Play.")); return; }
            }
        }
        catch (OperationCanceledException) { } // gave up after 30 minutes, or the button was pressed again
    });

    [RelayCommand]
    private void OpenBedrockData() { if (_bedrockStatus?.DataDirectory is { } d) _shell.OpenDirectory(d); }
}
