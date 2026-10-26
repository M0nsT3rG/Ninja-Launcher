using System.Text.Json;
using System.Text.RegularExpressions;

namespace NinjaLauncher.UnitTests;

/// <summary>Every UI string in the Desktop project must have a Portuguese entry with the same placeholders.</summary>
public sealed partial class LocalizationTests
{
    private static string Desktop()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NinjaLauncher.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "NinjaLauncher.Desktop");
    }

    private static IEnumerable<string> SourceFiles(string ext) => Directory.EnumerateFiles(Desktop(), "*" + ext, SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Every_ui_string_has_a_portuguese_translation()
    {
        var keys = SourceFiles(".axaml").SelectMany(f => XamlKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Concat(SourceFiles(".cs").SelectMany(f => CodeKey().Matches(File.ReadAllText(f)).Select(m => Regex.Unescape(m.Groups[1].Value))))
            .Distinct().ToList();
        var pt = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Desktop(), "Localization", "pt-PT.json")))!;

        Assert.True(keys.Count > 200, "key extraction looks broken");
        var untranslated = keys.Where(k => !pt.ContainsKey(k)).ToList();
        Assert.True(untranslated.Count == 0, "Missing pt-PT translations: " + string.Join(" | ", untranslated));
        foreach (var (en, tr) in pt)
            Assert.True(Placeholders(en).SequenceEqual(Placeholders(tr)), $"placeholders differ for: {en}");
    }

    [Fact]
    public void No_literal_ui_text_is_left_unwrapped_in_views()
    {
        var literal = new Regex("\\b(Text|Content|PlaceholderText|ToolTip\\.Tip)=\"([^\"{}]*[A-Za-z][^\"{}]*)\"");
        var leftovers = SourceFiles(".axaml").Where(f => !f.EndsWith("App.axaml"))
            .SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .Where(s => !s.Contains("\"✕\"") && !s.Contains("Text=\"Ninja\"") && !s.Contains("Text=\"LAUNCHER\"")).ToList(); // brand wordmark stays as is
        Assert.Empty(leftovers);
    }

    private static IEnumerable<string> Placeholders(string s) => Regex.Matches(s, @"\{\d+(?::[^}]*)?\}").Select(m => m.Value).Order();

    [GeneratedRegex(@"\{(?:l|loc):T '([^']*)'\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"L\.(?:T|F)\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeKey();
}
