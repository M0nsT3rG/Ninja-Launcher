using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaLauncher.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Called each time the page is shown; pages refresh their data here.</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;
}

public sealed record NavItem(string Title, string Icon, ViewModelBase Page);

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
        Downloads = ActivatorUtilities.CreateInstance<DownloadsViewModel>(services);
        Accounts = ActivatorUtilities.CreateInstance<AccountsViewModel>(services, Ui);
        Settings = ActivatorUtilities.CreateInstance<SettingsViewModel>(services, Ui);
        Navigation =
        [
            new("Home", "M10,20V14H14V20H19V12H22L12,3L2,12H5V20H10Z", Home),
            new("Library", "M4,6H2V20A2,2 0 0,0 4,22H18V20H4V6M20,2H8A2,2 0 0,0 6,4V16A2,2 0 0,0 8,18H20A2,2 0 0,0 22,16V4A2,2 0 0,0 20,2Z", Library),
            new("Discover", "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z", Discover),
            new("Downloads", "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z", Downloads),
            new("Accounts", "M12,4A4,4 0 0,1 16,8A4,4 0 0,1 12,12A4,4 0 0,1 8,8A4,4 0 0,1 12,4M12,14C16.42,14 20,15.79 20,18V20H4V18C4,15.79 7.58,14 12,14Z", Accounts),
            new("Settings", "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z", Settings),
        ];
        SelectedNav = Navigation[0];
    }

    public UiContext Ui { get; }
    public HomeViewModel Home { get; }
    public LibraryViewModel Library { get; }
    public DiscoverViewModel Discover { get; }
    public DownloadsViewModel Downloads { get; }
    public AccountsViewModel Accounts { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<NavItem> Navigation { get; }

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
    public void Info(string message) => shell.ShowToast(message, false);
    public void Error(string message) => shell.ShowToast(message, true);
    public Task<bool> ConfirmAsync(string text) => shell.AskAsync(text);
    public void Navigate(ViewModelBase page) => shell.Navigate(page);
    public MainViewModel Shell => shell;

    /// <summary>Runs an action and turns any exception into an error toast.</summary>
    public async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error(NinjaLauncher.Application.Security.SecretRedactor.Redact(e.Message)); }
    }
}
