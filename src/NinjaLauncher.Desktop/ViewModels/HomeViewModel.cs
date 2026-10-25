using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Bedrock;
using NinjaLauncher.Minecraft.Java.Launch;

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
    private BedrockStatus? _bedrockStatus;

    public HomeViewModel(UiContext ui, InstanceService instances, AccountService accounts, GameSessionService game,
        BedrockProvider bedrock, IActivityLog activity, SettingsHolder settings, IUrlLauncher shell)
    {
        (_ui, _instances, _accounts, _game, _bedrock, _activity, _settings, _shell) = (ui, instances, accounts, game, bedrock, activity, settings, shell);
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

    [ObservableProperty] public partial string AccountText { get; set; } = "Not signed in";
    [ObservableProperty] public partial string BedrockText { get; set; } = "";
    [ObservableProperty] public partial bool HasBedrockData { get; set; }

    public string SelectedSummary => SelectedInstance is { } i
        ? $"Minecraft {i.MinecraftVersion}{(i.Loader == ModLoaderKind.None ? "" : $" · {i.Loader} {i.LoaderVersion}")} · {i.MaxMemoryMb} MB" +
          (i.LastPlayedUtc is { } t ? $" · last played {t.LocalDateTime:g}" : "")
        : "Create an instance in the Library to start playing.";

    public override async Task OnShownAsync()
    {
        var active = await _accounts.GetActiveAsync();
        AccountText = active is null ? "Not signed in"
            : active.Kind == AccountKind.Offline ? $"Offline as {active.Username}" : $"Signed in as {active.Username}";
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
        BedrockText = "Checking for Bedrock Edition…";
        _bedrockStatus = await _bedrock.DetectAsync();
        BedrockText = _bedrockStatus.Message + (_bedrockStatus.Version is { } v ? $" (version {v})" : "");
        HasBedrockData = _bedrockStatus.DataDirectory is not null;
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
            _bedrock.Launch(_bedrockStatus!);
            await _activity.AddAsync("Launched Minecraft Bedrock Edition");
            return;
        }
        _ui.Info($"Preparing {SelectedInstance!.Name}… progress is on the Downloads page.");
        await _game.PlayAsync(SelectedInstance);
        await OnShownAsync();
    });

    [RelayCommand]
    private void OpenBedrockData() { if (_bedrockStatus?.DataDirectory is { } d) _shell.OpenDirectory(d); }
}
