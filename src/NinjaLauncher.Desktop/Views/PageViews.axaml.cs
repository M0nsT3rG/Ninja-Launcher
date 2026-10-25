using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NinjaLauncher.Desktop.Views;

public partial class HomeView : UserControl { public HomeView() => AvaloniaXamlLoader.Load(this); }
public partial class LibraryView : UserControl { public LibraryView() => AvaloniaXamlLoader.Load(this); }
public partial class DiscoverView : UserControl { public DiscoverView() => AvaloniaXamlLoader.Load(this); }
public partial class DownloadsView : UserControl { public DownloadsView() => AvaloniaXamlLoader.Load(this); }
public partial class AccountsView : UserControl { public AccountsView() => AvaloniaXamlLoader.Load(this); }
public partial class SettingsView : UserControl { public SettingsView() => AvaloniaXamlLoader.Load(this); }
