using System.Text.RegularExpressions;

namespace NinjaLauncher.Mods;

/// <summary>
/// Decides which instance files go into an exported modpack. Shared by the Modrinth and CurseForge exporters.
/// Never included: worlds, logs, screenshots, options/servers files, launcher files, anything reached through a
/// symlink/junction, and config files whose names suggest credentials or personal caches.
/// </summary>
public static partial class PackExportFiles
{
    /// <summary>Folders whose top-level files may be catalogue downloads (checked against Modrinth/CurseForge).</summary>
    public static readonly string[] ContentFolders = ["mods", "resourcepacks", "shaderpacks"];

    /// <summary>Pack configuration folders copied as overrides.</summary>
    public static readonly string[] OverrideFolders = ["config", "defaultconfigs", "kubejs", "scripts", "global_packs", "datapacks"];

    public sealed record Selection(IReadOnlyList<(string Relative, string FullPath)> Content,
        IReadOnlyList<(string Relative, string FullPath)> Overrides, int SkippedDisabled, int SkippedPrivate);

    public static Selection Collect(string gameDirectory)
    {
        var content = new List<(string, string)>();
        var overrides = new List<(string, string)>();
        int disabled = 0, @private = 0;

        foreach (var folder in ContentFolders)
        {
            var dir = new DirectoryInfo(Path.Combine(gameDirectory, folder));
            if (!dir.Exists || dir.LinkTarget is not null) continue;
            foreach (var file in dir.EnumerateFiles())
            {
                if (file.LinkTarget is not null || file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) { disabled++; continue; } // the formats have no "disabled" state
                content.Add(($"{folder}/{file.Name}", file.FullName));
            }
            // Unzipped resource/shader packs (folders) and mod sub-folders ship as overrides.
            foreach (var sub in dir.EnumerateDirectories())
                if (sub.LinkTarget is null) AddTree(gameDirectory, sub, overrides, ref @private);
        }
        foreach (var folder in OverrideFolders)
        {
            var dir = new DirectoryInfo(Path.Combine(gameDirectory, folder));
            if (dir.Exists && dir.LinkTarget is null) AddTree(gameDirectory, dir, overrides, ref @private);
        }
        return new Selection(content, overrides, disabled, @private);
    }

    private static void AddTree(string gameDirectory, DirectoryInfo dir, List<(string, string)> into, ref int skippedPrivate)
    {
        foreach (var file in dir.EnumerateFiles())
        {
            if (file.LinkTarget is not null) continue;
            if (LooksPrivate(file.Name)) { skippedPrivate++; continue; }
            into.Add((Path.GetRelativePath(gameDirectory, file.FullName).Replace('\\', '/'), file.FullName));
        }
        foreach (var sub in dir.EnumerateDirectories())
        {
            if (sub.LinkTarget is not null || sub.Name.StartsWith('.')) continue; // links, .git and other hidden folders
            if (LooksPrivate(sub.Name)) { skippedPrivate++; continue; }
            AddTree(gameDirectory, sub, into, ref skippedPrivate);
        }
    }

    internal static bool LooksPrivate(string name) => PrivateName().IsMatch(name);

    // e.g. ReAuth's reauth.toml (saved credentials), voice-chat username caches, session/token files.
    [GeneratedRegex("(?i)(token|auth|passw|credential|secret|session|cache|username|account|cookie|\\.key$|\\.pem$)")]
    private static partial Regex PrivateName();
}
