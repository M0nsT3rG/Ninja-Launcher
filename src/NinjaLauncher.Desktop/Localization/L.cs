using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Markup.Xaml;

namespace NinjaLauncher.Desktop.Localization;

/// <summary>
/// Gettext-style localisation: the English text is the key, translations live in embedded JSON
/// (<c>Localization/&lt;culture&gt;.json</c>). Missing entries fall back to English. The language applies on start-up.
/// </summary>
public static partial class L
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
        _templates = null;
    }

    private static List<(Regex Pattern, string Translation, HashSet<string> Nested)>? _templates;

    // Keys with placeholders ("Created instance '{0}' ({1})") as anchored patterns, so already-formatted messages
    // from the engine layers (exceptions, progress stages, activity entries) can be translated too.
    private static List<(Regex Pattern, string Translation, HashSet<string> Nested)> Templates() => _templates ??= _table
        .Where(kv => kv.Value.Length > 0 && Placeholder().IsMatch(kv.Key) && Placeholder().Replace(kv.Key, "").Trim().Length >= 4)
        .OrderByDescending(kv => Placeholder().Replace(kv.Key, "").Length) // most specific first
        .Select(kv => (new Regex("^" + string.Concat(Placeholder().Split(kv.Key).Select((part, i) => i % 2 == 0 ? Regex.Escape(part) : $"(?<p{part}>.*?)")) + "$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), kv.Value,
            Placeholder().Matches(kv.Key).Where(m => m.Index >= 2 && kv.Key[(m.Index - 2)..m.Index] == ": ").Select(m => m.Groups[1].Value).ToHashSet()))
        .ToList();

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static Dictionary<string, string> Load(string code)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"NinjaLauncher.Localization.{code}.json");
        return stream is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }

    public static string T(string english) => _table.Count == 0 ? english : Lookup(english, 0);

    private static string Lookup(string text, int depth)
    {
        if (_table.TryGetValue(text, out var t) && t.Length > 0) return t;
        if (depth > 2 || text.Length > 2000) return text;
        foreach (var (pattern, translation, nested) in Templates())
        {
            Match m;
            try { m = pattern.Match(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            // Filled-in values (names, versions, paths) are kept as they are. Only a value that the English key puts
            // after ": " is an embedded message ("Could not load versions: {0}") and is translated too.
            return Placeholder().Replace(translation, p => m.Groups["p" + p.Groups[1].Value].Value is var v && nested.Contains(p.Groups[1].Value) ? Lookup(v, depth + 1) : v);
        }
        return text;
    }

    /// <summary>Translates a composite format string ("Created '{0}'.") and fills it in.</summary>
    public static string F(string englishFormat, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);
}

/// <summary><c>Text="{l:T 'Welcome back'}"</c></summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(key);
}
