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
            // Last-resort guard: log and keep running instead of crashing on an unexpected UI exception.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Serilog.Log.Error(e.Exception, "Unhandled UI exception");
                e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Serilog.Log.Error(e.Exception, "Unobserved task exception");
                e.SetObserved();
            };
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
