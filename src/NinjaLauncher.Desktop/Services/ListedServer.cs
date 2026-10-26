using System.Text.Json;
using Microsoft.Extensions.Logging;
using NinjaLauncher.Minecraft.Java.Launch;

namespace NinjaLauncher.Desktop.Services;

/// <summary>
/// The server the owner pre-lists in Multiplayer. Its details are not in the public repository: they come from a
/// git-ignored <c>listed-server.json</c> embedded at build time (release builds write it from a repository secret).
/// Builds without the file list nothing.
/// </summary>
public sealed class ListedServer(ILogger<ListedServer> logger)
{
    private sealed record Config(string Name, string JavaAddress);

    private readonly Config? _config = Load();

    private static Config? Load()
    {
        using var stream = typeof(ListedServer).Assembly.GetManifestResourceStream("NinjaLauncher.listed-server.json");
        if (stream is null) return null;
        try
        {
            var c = JsonSerializer.Deserialize<Config>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return c is { Name.Length: > 0 and <= 64, JavaAddress.Length: > 0 and <= 255 } ? c : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Adds the server to this instance's list once; a player who deletes it keeps it deleted. Never blocks a launch.</summary>
    public void EnsureJava(string gameDirectory)
    {
        if (_config is null) return;
        try { ServerList.AddOnce(gameDirectory, _config.Name, _config.JavaAddress); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not update the instance's server list: {Error}", e.GetType().Name); // no address in logs
        }
    }
}
