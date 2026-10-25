using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NinjaLauncher.Desktop.Services;
using NinjaLauncher.Desktop.ViewModels;
using NinjaLauncher.Desktop.Views;

namespace NinjaLauncher.Desktop;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Build();
            var settings = _services.GetRequiredService<SettingsHolder>().Current;
            ThemeManager.Apply(settings.Theme, settings.AccentColor);
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(_services) };
            desktop.ShutdownRequested += (_, _) =>
            {
                Serilog.Log.CloseAndFlush();
                _services.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
