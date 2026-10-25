using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace NinjaLauncher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}

public static class Converters
{
    public static readonly IValueConverter Geometry = new FuncValueConverter<string, StreamGeometry?>(s => s is null ? null : StreamGeometry.Parse(s));

    public static readonly IValueConverter LocalTime = new FuncValueConverter<DateTimeOffset, string>(t => t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));

    public static readonly IValueConverter FileName = new FuncValueConverter<string, string?>(Path.GetFileName);

    public static readonly IValueConverter Megabytes = new FuncValueConverter<long, string>(b => $"{b / 1024.0 / 1024.0:0.#} MB");
}
