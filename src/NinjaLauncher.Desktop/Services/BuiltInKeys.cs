namespace NinjaLauncher.Desktop.Services;

/// <summary>
/// API keys built into release builds. They are never in the repository: release builds write them from repository
/// secrets into git-ignored files that are embedded at build time. Development and CI builds have none.
/// </summary>
public static class BuiltInKeys
{
    /// <summary>The owner's CurseForge key; a key the user saves in Settings takes precedence.</summary>
    public static string? CurseForge { get; } = Read("NinjaLauncher.curseforge-api.key");

    private static string? Read(string resource)
    {
        using var stream = typeof(BuiltInKeys).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        var key = new StreamReader(stream).ReadToEnd().Trim();
        return key.Length >= 20 && !key.Any(char.IsWhiteSpace) ? key : null;
    }
}
