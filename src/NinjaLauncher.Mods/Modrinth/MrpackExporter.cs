using System.IO.Compression;
using System.Text.Json;
using NinjaLauncher.Domain;

namespace NinjaLauncher.Mods.Modrinth;

public sealed record MrpackExportResult(string Path, int ModrinthFiles, int OverrideFiles, int SkippedDisabled, int SkippedPrivate = 0);

/// <summary>
/// Writes an instance as a Modrinth modpack (formatVersion 1). Content that Modrinth hosts is referenced by URL and
/// hash; everything else that belongs in a pack goes to <c>overrides/</c>. Personal and runtime data (worlds, logs,
/// screenshots, options, server list, crash reports, launcher files) is never included.
/// </summary>
public sealed class MrpackExporter(ModrinthClient modrinth)
{
    public async Task<MrpackExportResult> ExportAsync(GameInstance instance, string gameDirectory, string outputPath, string versionId,
        IProgress<double>? progress, CancellationToken ct)
    {
        var dependencies = new Dictionary<string, string> { ["minecraft"] = instance.MinecraftVersion };
        if (LoaderDependency(instance) is { } loader) dependencies[loader.Key] = loader.Value;

        var selection = PackExportFiles.Collect(gameDirectory);
        var candidates = selection.Content;
        var skippedDisabled = selection.SkippedDisabled;

        var hashes = new Dictionary<string, string>();
        foreach (var (relative, full) in candidates) hashes[relative] = await ModrinthClient.Sha1OfAsync(full, ct);
        progress?.Report(30);
        var versions = await modrinth.IdentifyFilesAsync(hashes.Values.Distinct().ToList(), ct);
        progress?.Report(50);

        var files = new List<object>();
        var overrides = new List<(string Relative, string FullPath)>();
        foreach (var (relative, full) in candidates)
        {
            var sha1 = hashes[relative];
            var match = versions.GetValueOrDefault(sha1)?.Files.FirstOrDefault(f => f.Hashes.GetValueOrDefault("sha1")?.Equals(sha1, StringComparison.OrdinalIgnoreCase) == true);
            if (match is not null && match.Hashes.TryGetValue("sha512", out var sha512) && Uri.TryCreate(match.Url, UriKind.Absolute, out var u) && MrpackImporter.AllowedHosts.Contains(u.Host))
                files.Add(new { path = relative, hashes = new { sha1, sha512 }, downloads = new[] { match.Url }, fileSize = new FileInfo(full).Length });
            else
                overrides.Add((relative, full));
        }
        overrides.AddRange(selection.Overrides);

        var index = new
        {
            formatVersion = 1, game = "minecraft", versionId, name = instance.Name,
            summary = $"Exported from Ninja Launcher ({instance.MinecraftVersion}{(instance.Loader == ModLoaderKind.None ? "" : " " + instance.Loader)})",
            files, dependencies,
        };

        var temp = outputPath + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                        writer.Write(JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
                    for (var i = 0; i < overrides.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(overrides[i].FullPath, "overrides/" + overrides[i].Relative, CompressionLevel.Optimal);
                        progress?.Report(50 + 50.0 * i / Math.Max(1, overrides.Count));
                    }
                }
                MrpackImporter.ReadIndex(temp); // self-check: the result must pass our own importer's validation
                File.Move(temp, outputPath, overwrite: true);
            }, ct);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return new MrpackExportResult(outputPath, files.Count, overrides.Count, skippedDisabled, selection.SkippedPrivate);
    }

    /// <summary>The modrinth.index.json dependency key and value for the instance's loader.</summary>
    internal static KeyValuePair<string, string>? LoaderDependency(GameInstance instance) => (instance.Loader, instance.LoaderVersion) switch
    {
        (ModLoaderKind.Fabric, { } v) => new("fabric-loader", v),
        (ModLoaderKind.Quilt, { } v) => new("quilt-loader", v),
        (ModLoaderKind.NeoForge, { } v) => new("neoforge", v),
        // Our Forge versions are maven ids "<mc>-<forge>"; the pack format wants just the Forge version.
        (ModLoaderKind.Forge, { } v) => new("forge", v.StartsWith(instance.MinecraftVersion + "-", StringComparison.Ordinal) ? v[(instance.MinecraftVersion.Length + 1)..] : v),
        _ => null,
    };
}
