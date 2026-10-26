using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NinjaLauncher.Minecraft.Bedrock;

/// <summary>
/// Adds one server to Bedrock's Servers tab once per profile. The game keeps the list in
/// <c>minecraftpe/external_servers.txt</c>, one <c>index:name:host:port:unix-seconds</c> line per server (checked
/// against a file written by 1.26.52). Existing lines are kept byte for byte, a file we can't parse is left alone,
/// and a marker file means a server the player deletes is not added back. Never written while the game runs,
/// because the game rewrites the file when it closes.
/// </summary>
public static class BedrockServerList
{
    public const string FileName = "external_servers.txt";
    public const string MarkerFileName = ".ninja-listed-server";

    /// <summary>Profile folders that already exist (the game creates them on first start).</summary>
    public static IReadOnlyList<string> ProfileDirectories()
    {
        var dirs = new List<string>();
        if (!OperatingSystem.IsWindows()) return dirs; // shortcut: mcpelauncher profiles on Linux/macOS are not handled yet
        var gdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Minecraft Bedrock", "Users");
        if (Directory.Exists(gdk))
            foreach (var user in Directory.EnumerateDirectories(gdk))
                if (Path.Combine(user, "games", "com.mojang", "minecraftpe") is var pe && Directory.Exists(pe)) dirs.Add(pe);
        var uwp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe", "LocalState", "games", "com.mojang", "minecraftpe");
        if (Directory.Exists(uwp)) dirs.Add(uwp);
        return dirs;
    }

    public static bool IsGameRunning() =>
        Process.GetProcessesByName("Minecraft.Windows").Length > 0 || Process.GetProcessesByName("mcpelauncher-client").Length > 0;

    /// <returns>True when the server was added now.</returns>
    public static bool AddOnce(string minecraftpeDirectory, string name, string host, int port, DateTimeOffset now)
    {
        if (name.Length == 0 || host.Length == 0 || $"{name}{host}".IndexOfAny([':', '\n', '\r']) >= 0 || port is < 1 or > 65535)
            throw new ArgumentException("The listed server's name and host must be non-empty without ':' or line breaks.");
        var marker = Path.Combine(minecraftpeDirectory, MarkerFileName);
        if (File.Exists(marker)) return false;

        var path = Path.Combine(minecraftpeDirectory, FileName);
        var existing = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (existing.Length > 1024 * 1024) return false;
        var text = Encoding.UTF8.GetString(existing);

        var maxIndex = 0;
        var already = false;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
        {
            var f = line.Split(':');
            if (f.Length < 4 || !int.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false; // not a format we know
            maxIndex = Math.Max(maxIndex, index);
            already |= f[2].Equals(host, StringComparison.OrdinalIgnoreCase) && f[3] == port.ToString(CultureInfo.InvariantCulture);
        }

        if (!already)
        {
            var add = $"{maxIndex + 1}:{name}:{host}:{port}:{now.ToUnixTimeSeconds()}\n";
            var prefix = existing.Length > 0 && existing[^1] != (byte)'\n' ? "\n" : "";
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, [.. existing, .. Encoding.UTF8.GetBytes(prefix + add)]);
            File.Move(temp, path, overwrite: true);
        }
        File.WriteAllText(marker, "Ninja Launcher added its listed server to external_servers.txt once; delete this file to offer it again.\n");
        return !already;
    }
}
