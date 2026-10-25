using System.Text.RegularExpressions;

namespace NinjaLauncher.Application.Security;

/// <summary>Removes bearer tokens, JWTs and OAuth codes from text before it is logged or displayed.</summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)((?:access|refresh|id)_?token|code_verifier|password|client_secret|identityToken)([""'\s:=]+)([^\s""'&,;}]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?i)([?&]code=)[^&\s]+")]
    private static partial Regex QueryCode();

    [GeneratedRegex(@"(--accessToken\s+|XBL3\.0 x=)[^\s""']+")]
    private static partial Regex LaunchArg();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = Jwt().Replace(text, "[REDACTED-JWT]");
        text = LaunchArg().Replace(text, "$1[REDACTED]");
        text = QueryCode().Replace(text, "$1[REDACTED]");
        return KeyValue().Replace(text, "$1$2[REDACTED]");
    }
}
