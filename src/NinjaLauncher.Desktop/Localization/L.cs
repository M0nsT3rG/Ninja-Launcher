using System.Globalization;
using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace NinjaLauncher.Desktop.Localization;

/// <summary>
/// Gettext-style localisation: the English text is the key, translations live in embedded JSON
/// (<c>Localization/&lt;culture&gt;.json</c>). Missing entries fall back to English. The language applies on start-up.
/// </summary>
public static class L
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("auto", "System / Sistema"), ("en", "English"), ("pt-PT", "Português (Portugal)")];

    private static Dictionary<string, string> _table = new();

    public static string Current { get; private set; } = "en";

    /// <summary>"auto" picks Portuguese for any pt-* system culture, otherwise English.</summary>
    public static void Use(string? code)
    {
        var resolved = code is null or "" or "auto"
            ? CultureInfo.CurrentUICulture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-PT" : "en"
            : code;
        Current = resolved;
        _table = resolved == "en" ? new() : Load(resolved);
    }

    private static Dictionary<string, string> Load(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"NinjaLauncher.Localization.{code}.json");
        return stream is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }

    public static string T(string english) => _table.TryGetValue(english, out var t) && t.Length > 0 ? t : english;

    /// <summary>Translates a composite format string ("Created '{0}'.") and fills it in.</summary>
    public static string F(string englishFormat, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);
}

/// <summary><c>Text="{l:T 'Welcome back'}"</c></summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(key);
}
