using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using NinjaLauncher.Domain;
using NinjaLauncher.Mods.Modrinth;

namespace NinjaLauncher.Mods.CurseForge;

public sealed record CurseForgeExportResult(string Path, int LinkedFiles, int OverrideFiles, int SkippedDisabled, bool CurseForgeChecked);

/// <summary>
/// Writes an instance as a CurseForge modpack zip (manifest.json + overrides/). Files found on CurseForge (by
/// fingerprint, confirmed by SHA-1) are referenced by project/file id; everything else is bundled as an override.
/// Without an API key nothing can be looked up, so all content is bundled. Personal and runtime data is never included.
/// </summary>
public sealed class CurseForgeExporter(CurseForgeClient curseForge)
{
    private static readonly string[] ContentFolders = ["mods", "resourcepacks", "shaderpacks"];
    private static readonly string[] OverrideFolders = ["config", "defaultconfigs", "kubejs", "scripts", "global_packs", "datapacks"];

    public async Task<CurseForgeExportResult> ExportAsync(GameInstance instance, string gameDirectory, string outputPath, string version,
        IProgress<double>? progress, CancellationToken ct)
    {
        var candidates = new List<(string Relative, string FullPath)>();
        var skippedDisabled = 0;
        foreach (var folder in ContentFolders)
        {
            var dir = Path.Combine(gameDirectory, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) { skippedDisabled++; continue; }
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(($"{folder}/{Path.GetFileName(file)}", file));
            }
        }

        var linked = new List<object>();
        var overrides = new List<(string Relative, string FullPath)>();
        var checkedOnline = await curseForge.HasKeyAsync(ct);
        if (checkedOnline && candidates.Count > 0)
        {
            var fingerprints = new Dictionary<(string, string), uint>();
            foreach (var c in candidates) fingerprints[c] = await Task.Run(() => CurseForgeFingerprint.OfFile(c.FullPath), ct);
            progress?.Report(30);
            var matches = await curseForge.MatchFingerprintsAsync(fingerprints.Values.Distinct().ToList(), ct);
            progress?.Report(50);
            foreach (var c in candidates)
            {
                // A fingerprint match only counts if CurseForge's SHA-1 also agrees with our file.
                if (matches.TryGetValue(fingerprints[c], out var file) && file.Sha1 is { } sha1 &&
                    sha1.Equals(await ModrinthClient.Sha1OfAsync(c.FullPath, ct), StringComparison.OrdinalIgnoreCase))
                    linked.Add(new { projectID = file.ModId, fileID = file.Id, required = true });
                else overrides.Add(c);
            }
        }
        else overrides.AddRange(candidates);

        foreach (var folder in OverrideFolders)
        {
            var dir = Path.Combine(gameDirectory, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).LinkTarget is not null) continue;
                overrides.Add((Path.GetRelativePath(gameDirectory, file).Replace('\\', '/'), file));
            }
        }

        var manifest = new
        {
            minecraft = new { version = instance.MinecraftVersion, modLoaders = LoaderId(instance) is { } id ? new[] { new { id, primary = true } } : [] },
            manifestType = "minecraftModpack", manifestVersion = 1,
            name = instance.Name, version, author = "", files = linked, overrides = "overrides",
        };

        var temp = outputPath + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                        writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                    for (var i = 0; i < overrides.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(overrides[i].FullPath, "overrides/" + overrides[i].Relative, CompressionLevel.Optimal);
                        progress?.Report(50 + 50.0 * i / Math.Max(1, overrides.Count));
                    }
                }
                CurseForgeModpackInstaller.ReadManifest(temp); // self-check with our own importer's validation
                File.Move(temp, outputPath, overwrite: true);
            }, ct);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return new CurseForgeExportResult(outputPath, linked.Count, overrides.Count, skippedDisabled, checkedOnline);
    }

    /// <summary>The manifest loader id, e.g. "forge-47.2.0" (our Forge ids are "&lt;mc&gt;-&lt;forge&gt;").</summary>
    internal static string? LoaderId(GameInstance instance) => (instance.Loader, instance.LoaderVersion) switch
    {
        (ModLoaderKind.Forge, { } v) => "forge-" + (v.StartsWith(instance.MinecraftVersion + "-", StringComparison.Ordinal) ? v[(instance.MinecraftVersion.Length + 1)..] : v),
        (ModLoaderKind.NeoForge, { } v) => "neoforge-" + v,
        (ModLoaderKind.Fabric, { } v) => "fabric-" + v,
        (ModLoaderKind.Quilt, { } v) => "quilt-" + v,
        _ => null,
    };
}

/// <summary>
/// CurseForge file fingerprint: 32-bit MurmurHash2 (seed 1) over the file with whitespace bytes (9, 10, 13, 32) removed.
/// shortcut: not in the official docs (community-documented); exports confirm every match by SHA-1, so a wrong value only bundles the file.
/// </summary>
public static class CurseForgeFingerprint
{
    public static uint OfFile(string path) => Compute(File.ReadAllBytes(path));

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var filtered = new byte[data.Length];
        var n = 0;
        foreach (var b in data) if (b is not (9 or 10 or 13 or 32)) filtered[n++] = b;
        return MurmurHash2(filtered.AsSpan(0, n), 1);
    }

    internal static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;
        var h = seed ^ (uint)data.Length;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var k = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            k *= m; k ^= k >> r; k *= m;
            h *= m; h ^= k;
        }
        switch (data.Length - i)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }
        h ^= h >> 13; h *= m; h ^= h >> 15;
        return h;
    }
}
