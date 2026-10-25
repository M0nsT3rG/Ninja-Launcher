using System.IO.Compression;

namespace NinjaLauncher.Application.Security;

/// <summary>Zip extraction with path-traversal, entry-count and decompression-bomb limits.</summary>
public static class SafeArchive
{
    public const long DefaultMaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int DefaultMaxEntries = 200_000;

    /// <param name="filter">Optional predicate on the entry's full name; return false to skip.</param>
    public static void ExtractToDirectory(string zipPath, string destination, Func<string, bool>? filter = null,
        long maxTotalBytes = DefaultMaxTotalBytes, int maxEntries = DefaultMaxEntries, bool overwrite = true)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        Extract(archive, destination, filter, maxTotalBytes, maxEntries, overwrite);
    }

    public static void Extract(ZipArchive archive, string destination, Func<string, bool>? filter = null,
        long maxTotalBytes = DefaultMaxTotalBytes, int maxEntries = DefaultMaxEntries, bool overwrite = true)
    {
        if (archive.Entries.Count > maxEntries) throw new InvalidDataException($"Archive has too many entries ({archive.Entries.Count}).");
        Directory.CreateDirectory(destination);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (filter is not null && !filter(entry.FullName)) continue;
            var target = SafePath.Combine(destination, entry.FullName.Replace('\\', '/'));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!overwrite && File.Exists(target)) continue;
            using var input = entry.Open();
            using var output = File.Create(target);
            // Declared sizes can lie, so count real bytes while copying.
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                total += read;
                if (total > maxTotalBytes) throw new InvalidDataException("Archive exceeds the maximum extracted size.");
                output.Write(buffer, 0, read);
            }
        }
    }
}
