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
        if (DataContext is not MainViewModel vm) return;
        vm.Ui.FilePicker = PickFileAsync;
        vm.Ui.SavePicker = SaveFileAsync;
    }

    private async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickFileAsync(string title, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = pattern.Split(';') }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}

public static class Converters
{
    public static readonly IValueConverter Geometry = new FuncValueConverter<string, StreamGeometry?>(s => s is null ? null : StreamGeometry.Parse(s));

    public static readonly IValueConverter LocalTime = new FuncValueConverter<DateTimeOffset, string>(t => t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));

    public static readonly IValueConverter FileName = new FuncValueConverter<string, string?>(Path.GetFileName);

    public static readonly IValueConverter IsError = new FuncValueConverter<NinjaLauncher.Mods.Modrinth.IssueSeverity, bool>(s => s == NinjaLauncher.Mods.Modrinth.IssueSeverity.Error);

    public static readonly IValueConverter JoinLines = new FuncValueConverter<IReadOnlyList<string>, string>(l => l is null ? "" : string.Join(", ", l));
}
