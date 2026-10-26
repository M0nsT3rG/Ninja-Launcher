using System.Text;
using System.Text.RegularExpressions;

namespace NinjaLauncher.UnitTests;

/// <summary>
/// Minimal C# string-literal scanner for the localisation tests: yields regular, verbatim and interpolated literals
/// (interpolation holes become {0}, {1}…, keeping simple format specifiers such as :N0), skipping comments, char
/// literals and raw strings.
/// </summary>
internal static class EngineStrings
{
    public static IEnumerable<(int Start, string Text)> Literals(string src)
    {
        var i = 0;
        while (i < src.Length)
        {
            if (At(src, i, "//")) { i = src.IndexOf('\n', i) is var j && j < 0 ? src.Length : j; continue; }
            if (At(src, i, "/*")) { i = src.IndexOf("*/", i, StringComparison.Ordinal) is var j && j < 0 ? src.Length : j + 2; continue; }
            if (At(src, i, "\"\"\"") || At(src, i, "$\"\"\"")) { i = src.IndexOf("\"\"\"", i + 4, StringComparison.Ordinal) is var j && j < 0 ? src.Length : j + 3; continue; }
            if (src[i] == '\'') { i = SkipQuoted(src, i, '\''); continue; }
            var interpolated = At(src, i, "$\"") || At(src, i, "$@\"") || At(src, i, "@$\"");
            var verbatim = At(src, i, "@\"") || At(src, i, "$@\"") || At(src, i, "@$\"");
            if (src[i] != '"' && !interpolated && !verbatim) { i++; continue; }

            var start = i;
            i = src.IndexOf('"', i) + 1;
            var text = new StringBuilder();
            var holes = 0;
            while (i < src.Length)
            {
                var c = src[i];
                if (c == '\\' && !verbatim) { text.Append(Unescape(src[i + 1])); i += 2; continue; }
                if (c == '"' && verbatim && At(src, i, "\"\"")) { text.Append('"'); i += 2; continue; }
                if (c == '"') { i++; break; }
                if (interpolated && At(src, i, "{{")) { text.Append('{'); i += 2; continue; }
                if (interpolated && At(src, i, "}}")) { text.Append('}'); i += 2; continue; }
                if (interpolated && c == '{')
                {
                    var (end, expression) = ReadHole(src, i + 1);
                    var format = Regex.Match(expression, @":([A-Za-z]\d*)$") is { Success: true } m && !expression.Contains('?') ? ":" + m.Groups[1].Value : "";
                    text.Append('{').Append(holes++).Append(format).Append('}');
                    i = end + 1;
                    continue;
                }
                text.Append(c);
                i++;
            }
            yield return (start, text.ToString());
        }
    }

    private static bool At(string s, int i, string token) => string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    private static int SkipQuoted(string s, int i, char quote)
    {
        for (i++; i < s.Length && s[i] != quote; i++) if (s[i] == '\\') i++;
        return i + 1;
    }

    /// <summary>Returns the index of the closing brace and the hole's expression text (nested strings and braces included).</summary>
    private static (int End, string Expression) ReadHole(string s, int i)
    {
        var depth = 1;
        var start = i;
        while (i < s.Length)
        {
            if (s[i] == '"') { i = SkipQuoted(s, i, '"'); continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return (i, s[start..i]);
            i++;
        }
        return (s.Length - 1, s[start..]);
    }

    private static char Unescape(char c) => c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => c };
}
