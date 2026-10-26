using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaLauncher.Application.Abstractions;
using NinjaLauncher.Application.Services;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Domain;
using NinjaLauncher.Minecraft.Java.Launch;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly UiContext _ui;
    private readonly InstanceService _instances;
    private readonly AccountService _accounts;
    private readonly GameSessionService _game;
    private readonly IActivityLog _activity;
    private readonly SettingsHolder _settings;

    public HomeViewModel(UiContext ui, InstanceService instances, AccountService accounts, GameSessionService game,
        IActivityLog activity, SettingsHolder settings)
    {
        (_ui, _instances, _accounts, _game, _activity, _settings) = (ui, instances, accounts, game, activity, settings);
        _game.RunningChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => PlayCommand.NotifyCanExecuteChanged());
    }

    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<GameLogLine> GameLog => _game.Log;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedSummary)), NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial GameInstance? SelectedInstance { get; set; }

    [ObservableProperty] public partial string AccountText { get; set; } = L.T("Not signed in");

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
    }

    partial void OnSelectedInstanceChanged(GameInstance? value)
    {
        if (value is not null && value.Id != _settings.Current.SelectedInstanceId)
            _ = _ui.RunAsync(() => _settings.UpdateAsync(s => s with { SelectedInstanceId = value.Id }));
    }

    private bool CanPlay() => SelectedInstance is { } i && !_game.IsRunning(i.Id);

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task Play() => _ui.RunAsync(async () =>
    {
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
}
