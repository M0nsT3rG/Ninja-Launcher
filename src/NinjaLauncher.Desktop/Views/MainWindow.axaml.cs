using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NinjaLauncher.Desktop.ViewModels;

namespace NinjaLauncher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm) vm.Ui.FilePicker = PickFileAsync;
    }

    private async Task<string?> PickFileAsync(string title, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}

public static class Converters
{
    public static readonly IValueConverter Geometry = new FuncValueConverter<string, StreamGeometry?>(s => s is null ? null : StreamGeometry.Parse(s));

    public static readonly IValueConverter LocalTime = new FuncValueConverter<DateTimeOffset, string>(t => t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));

    public static readonly IValueConverter FileName = new FuncValueConverter<string, string?>(Path.GetFileName);
}
