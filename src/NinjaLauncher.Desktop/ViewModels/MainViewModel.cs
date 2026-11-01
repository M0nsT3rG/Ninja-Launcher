using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

using NinjaLauncher.Desktop.Localization;

namespace NinjaLauncher.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Called each time the page is shown; pages refresh their data here.</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;
}

public sealed record NavItem(string Title, string Icon, ViewModelBase Page)
{
    public override string ToString() => Title; // what screen readers announce for the list item
}

/// <summary>Shell: navigation, toast messages and the confirmation dialog shared by all pages.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _confirm;

    public MainViewModel(IServiceProvider services)
    {
        Ui = new UiContext(this);
        Home = ActivatorUtilities.CreateInstance<HomeViewModel>(services, Ui);
        Library = ActivatorUtilities.CreateInstance<LibraryViewModel>(services, Ui);
        Discover = ActivatorUtilities.CreateInstance<DiscoverViewModel>(services, Ui);
        Wardrobe = ActivatorUtilities.CreateInstance<WardrobeViewModel>(services, Ui);
        Downloads = ActivatorUtilities.CreateInstance<DownloadsViewModel>(services);
        Accounts = ActivatorUtilities.CreateInstance<AccountsViewModel>(services, Ui);
        Settings = ActivatorUtilities.CreateInstance<SettingsViewModel>(services, Ui);
        Navigation =
        [
            new(L.T("Home"), "M10,20V14H14V20H19V12H22L12,3L2,12H5V20H10Z", Home),
            new(L.T("Profiles"), "M3,11H11V3H3M5,5H9V9H5M3,21H11V13H3M5,15H9V19H5M13,21H21V13H13M15,15H19V19H15M13,3V11H21V3M15,5H19V9H15Z", Library),
            new(L.T("Mods"), "M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5M12,4.15L6.04,7.5L12,10.85L17.96,7.5L12,4.15M5,15.91L11,19.29V12.58L5,9.21V15.91M19,15.91V9.21L13,12.58V19.29L19,15.91Z", Discover),
            new(L.T("Wardrobe"), "M16,21H8A1,1 0 0,1 7,20V12.07L5.7,13.07C5.31,13.46 4.68,13.46 4.29,13.07L1.46,10.29C1.07,9.9 1.07,9.27 1.46,8.88L7.34,3H9C9,4.1 10.34,5 12,5C13.66,5 15,4.1 15,3H16.66L22.54,8.88C22.93,9.27 22.93,9.9 22.54,10.29L19.71,13.12C19.32,13.5 18.69,13.5 18.3,13.12L17,12.12V20A1,1 0 0,1 16,21Z", Wardrobe),
            new(L.T("Downloads"), "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z", Downloads),
            new(L.T("Accounts"), "M12,4A4,4 0 0,1 16,8A4,4 0 0,1 12,12A4,4 0 0,1 8,8A4,4 0 0,1 12,4M12,14C16.42,14 20,15.79 20,18V20H4V18C4,15.79 7.58,14 12,14Z", Accounts),
            new(L.T("Settings"), "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z", Settings),
        ];
        RailItems = Navigation.Where(n => n.Page != Settings).ToList();
        SelectedNav = Navigation[0];

        var updates = services.GetRequiredService<NinjaLauncher.Desktop.Services.UpdateService>();
        if (updates.IsAvailable && services.GetRequiredService<NinjaLauncher.Desktop.Services.SettingsHolder>().Current.CheckForUpdates)
            _ = Ui.RunAsync(async () =>
            {
                if (await updates.CheckQuietlyAsync() is { } version) Ui.Info(L.F("Ninja Launcher {0} is available. Install it from Settings → Updates.", version));
            });
    }

    public UiContext Ui { get; }
    public HomeViewModel Home { get; }
    public LibraryViewModel Library { get; }
    public DiscoverViewModel Discover { get; }
    public WardrobeViewModel Wardrobe { get; }
    public DownloadsViewModel Downloads { get; }
    public AccountsViewModel Accounts { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<NavItem> Navigation { get; }

    /// <summary>The rail's icons; Settings sits apart at the bottom.</summary>
    public IReadOnlyList<NavItem> RailItems { get; }

    /// <summary>The rail's selection: nothing is highlighted there while Settings is open.</summary>
    public NavItem? RailSelection
    {
        get => SelectedNav is { } n && n.Page != Settings ? n : null;
        set { if (value is not null) SelectedNav = value; }
    }

    public bool IsSettingsOpen => SelectedNav?.Page == Settings;

    [RelayCommand] private void OpenSettings() => Navigate(Settings);

    /// <summary>The rail's play button: launches the most recently played profile.</summary>
    [RelayCommand] private Task PlayLatest() => Ui.RunAsync(Home.PlayLatestAsync);

    [ObservableProperty] public partial NavItem? SelectedNav { get; set; }
    [ObservableProperty] public partial ViewModelBase? CurrentPage { get; set; }
    [ObservableProperty] public partial string? Toast { get; set; }
    [ObservableProperty] public partial bool ToastIsError { get; set; }
    [ObservableProperty] public partial string? ConfirmText { get; set; }
    public bool IsConfirmOpen => ConfirmText is not null;

    partial void OnConfirmTextChanged(string? value) => OnPropertyChanged(nameof(IsConfirmOpen));

    async partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        OnPropertyChanged(nameof(RailSelection));
        OnPropertyChanged(nameof(IsSettingsOpen));
        CurrentPage = value.Page;
        await Ui.RunAsync(value.Page.OnShownAsync);
    }

    public void Navigate(ViewModelBase page) => SelectedNav = Navigation.First(n => n.Page == page);

    internal async void ShowToast(string message, bool isError)
    {
        Toast = message;
        ToastIsError = isError;
        await Task.Delay(isError ? 9000 : 4000);
        if (Toast == message) Toast = null;
    }

    internal Task<bool> AskAsync(string text)
    {
        _confirm?.TrySetResult(false);
        _confirm = new TaskCompletionSource<bool>();
        ConfirmText = text;
        return _confirm.Task;
    }

    [RelayCommand]
    private void Answer(string yes)
    {
        ConfirmText = null;
        _confirm?.TrySetResult(yes == "yes");
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;
}

/// <summary>What pages need from the shell: error reporting, confirmations, navigation.</summary>
public sealed class UiContext(MainViewModel shell)
{
    // L.T here also translates already-formatted messages that come from the engine layers.
    public void Info(string message) => shell.ShowToast(Localization.L.T(message), false);
    public void Error(string message) => shell.ShowToast(Localization.L.T(message), true);
    public Task<bool> ConfirmAsync(string text) => shell.AskAsync(text);
    public void Navigate(ViewModelBase page) => shell.Navigate(page);
    public MainViewModel Shell => shell;

    /// <summary>Set by the main window: (title, pattern) → chosen local file path, or null if cancelled.</summary>
    public Func<string, string, Task<string?>>? FilePicker { get; set; }

    public Task<string?> PickFileAsync(string title, string pattern) =>
        FilePicker?.Invoke(title, pattern) ?? Task.FromResult<string?>(null);

    /// <summary>Set by the main window: (title, suggested name) → chosen save path, or null if cancelled.</summary>
    public Func<string, string, Task<string?>>? SavePicker { get; set; }

    public Task<string?> SaveFileAsync(string title, string suggestedName) =>
        SavePicker?.Invoke(title, suggestedName) ?? Task.FromResult<string?>(null);

    /// <summary>Runs an action and turns any exception into an error toast.</summary>
    public async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error(NinjaLauncher.Application.Security.SecretRedactor.Redact(e.Message)); }
    }
}
