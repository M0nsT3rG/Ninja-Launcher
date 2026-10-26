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

/// <summary><c>{Binding X, Converter={x:Static v:Converters.Localize}, ConverterParameter='by {0}'}</c></summary>
public sealed class LocalizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || parameter is not string format ? null
            : NinjaLauncher.Desktop.Localization.L.F(format.StartsWith("{}", StringComparison.Ordinal) ? format[2..] : format, value); // '{}' is the XAML escape for a leading brace

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Converters
{
    public static readonly IValueConverter Localize = new LocalizeConverter();

    public static readonly IValueConverter Geometry = new FuncValueConverter<string, StreamGeometry?>(s => s is null ? null : StreamGeometry.Parse(s));

    public static readonly IValueConverter LocalTime = new FuncValueConverter<DateTimeOffset, string>(t => t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));

    public static readonly IValueConverter FileName = new FuncValueConverter<string, string?>(Path.GetFileName);

    public const string DefaultInstanceIcon = "avares://NinjaLauncher/Assets/ninja-launcher.png";

    /// <summary>Set at startup: instance → custom icon path (or null).</summary>
    public static Func<NinjaLauncher.Domain.GameInstance, string?>? InstanceIconResolver { get; set; }

    public static readonly IValueConverter InstanceIcon = new FuncValueConverter<NinjaLauncher.Domain.GameInstance?, string?>(i =>
        i is null ? null : InstanceIconResolver?.Invoke(i) ?? DefaultInstanceIcon);

    public static readonly IValueConverter IsError = new FuncValueConverter<NinjaLauncher.Mods.Modrinth.IssueSeverity, bool>(s => s == NinjaLauncher.Mods.Modrinth.IssueSeverity.Error);

    public static readonly IValueConverter JoinLines = new FuncValueConverter<IReadOnlyList<string>, string>(l => l is null ? "" : string.Join(", ", l));
}
