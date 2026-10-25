namespace NinjaLauncher.Application.Security;

/// <summary>Filesystem boundary checks. Every path built from remote or user data goes through here.</summary>
public static class SafePath
{
    /// <summary>Combines <paramref name="relative"/> under <paramref name="root"/> and throws if the result escapes it.</summary>
    public static string Combine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new UnsafePathException("Empty relative path.");
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new UnsafePathException($"Absolute path not allowed: {relative}");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!IsUnder(fullRoot, full)) throw new UnsafePathException($"Path escapes its root: {relative}");
        return full;
    }

    public static bool IsUnder(string root, string candidate)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.StartsWith(r, cmp);
    }

    /// <summary>Turns user text into a safe single directory/file name.</summary>
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? "unnamed" : cleaned[..Math.Min(cleaned.Length, 80)];
    }
}

public sealed class UnsafePathException(string message) : Exception(message);
