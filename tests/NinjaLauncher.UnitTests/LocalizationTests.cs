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
        var keys = SourceFiles(".axaml").SelectMany(f => XamlKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)
                .Concat(ConverterKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value.StartsWith("{}") ? m.Groups[1].Value[2..] : m.Groups[1].Value)))
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

        // StringFormat bypasses translation; only brand-neutral formats may use it.
        var formats = SourceFiles(".axaml").SelectMany(f => Regex.Matches(File.ReadAllText(f), "StringFormat='([^']*)'").Select(m => m.Groups[1].Value))
            .Where(f => Regex.IsMatch(f, "[A-Za-z]") && f is not ("Java {0}" or "Ninja Launcher {0}")).ToList();
        Assert.Empty(formats);
    }

    // Literals the engine scan picks up that are never shown in the launcher: protocol values, folder and vendor
    // names, file content, and the English-only page the sign-in browser tab shows.
    private static readonly HashSet<string> NotUiText =
    [
        "XboxLive.signin offline_access", "NinjaLauncher/{0} (+https://github.com/M0nsT3rG/Ninja-Launcher)",
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        "Application Support", "Eclipse Adoptium", "Amazon Corretto", "Windows DPAPI", "Minecraft Bedrock",
        "Exported from Ninja Launcher ({0}{1})", "Caused by: ", "Ninja Launcher",
        // servers.dat parsing: caught internally, never shown
        "Bad NBT array length.", "Bad NBT list length.", "NBT nested too deeply.", "NBT string too long.", "Truncated NBT.", "Unexpected NBT value.",
        "Unknown NBT tag {0}.", "Ninja Launcher added its listed server to servers.dat once; delete this file to offer it again.\n",
    ];

    /// <summary>
    /// Engine layers can't reference the Desktop translator, so their messages (exceptions, progress stages, mod
    /// issues, crash hints, activity entries) are translated where they are shown. Each one needs a pt-PT template
    /// with interpolation holes written as {0}, {1}…
    /// </summary>
    [Fact]
    public void Every_engine_message_has_a_portuguese_translation()
    {
        var src = Path.GetDirectoryName(Desktop())!;
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var desktop = file.StartsWith(Desktop(), StringComparison.Ordinal);
            foreach (var (start, literal) in EngineStrings.Literals(text))
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
                var lineEnd = text.IndexOf('\n', start) is var e && e < 0 ? text.Length : e;
                var line = text[lineStart..lineEnd];
                var before = text[lineStart..start];
                // Desktop UI text is covered by the test above; activity entries, progress stages and exceptions may skip L.T.
                if (desktop && !Regex.IsMatch(line, @"AddAsync\(|Report\(new\(|throw new ")) continue;
                if (NonUiLine().IsMatch(line) || NonUiArgument().IsMatch(before)) continue;
                if (!UiLike().IsMatch(literal) || !literal.Contains(' ') || !Regex.IsMatch(Regex.Replace(literal, @"\{\d+[^}]*\}", ""), "[a-z]{3}")) continue;
                if (NotUiText.Contains(literal)) continue;
                keys.Add(literal);
            }
        }
        var pt = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Desktop(), "Localization", "pt-PT.json")))!;
        Assert.True(keys.Count > 150, $"engine string extraction looks broken ({keys.Count})");
        var missing = keys.Where(k => !pt.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing pt-PT translations: " + string.Join(" | ", missing));
    }

    [GeneratedRegex(@"\bLog(Information|Warning|Error|Debug|Trace|Critical)?\(|Log\.\w+\(|_logger|logger\.|GeneratedRegex|SELECT |INSERT |CREATE |PRAGMA |DELETE FROM |UPDATE \w+ SET ")]
    private static partial Regex NonUiLine();

    [GeneratedRegex(@"(\.(Contains|StartsWith|EndsWith|IndexOf|Equals)\(|Respond\([^""]*)\s*$")]
    private static partial Regex NonUiArgument();

    [GeneratedRegex(@"^[A-Z'({]")]
    private static partial Regex UiLike();

    private static IEnumerable<string> Placeholders(string s) => Regex.Matches(s, @"\{\d+(?::[^}]*)?\}").Select(m => m.Value).Order();

    [GeneratedRegex(@"\{(?:l|loc):T '([^']*)'\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"ConverterParameter='([^']*)'")]
    private static partial Regex ConverterKey();

    [GeneratedRegex(@"L\.(?:T|F)\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeKey();
}
