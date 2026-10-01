using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MangaReader;

public sealed class ArchiveReader
{
    public const long MaxPageBytes = 40 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif" };

    public static bool Supports(string? path) => path is not null &&
        new[] { ".cbz", ".zip" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static List<ZipArchiveEntry> Pages(ZipArchive archive)
    {
        if (archive.Entries.Count > 20000) throw new InvalidDataException("Archive has too many entries.");
        var pages = archive.Entries.Where(e => Extensions.Contains(Path.GetExtension(e.FullName)) &&
            !e.FullName.Replace('\\', '/').Split('/').Any(p => p.StartsWith('.') || p == "__MACOSX"))
            .OrderBy(e => e.FullName, NaturalComparer.Instance).ToList();
        if (pages.Count == 0) throw new InvalidDataException("No supported images found in this archive.");
        return pages;
    }

    public int Count(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return Pages(archive).Count;
    }

    public async Task<(byte[] Data, string Mime)> Read(string path, int page, CancellationToken cancel)
    {
        using var archive = ZipFile.OpenRead(path);
        var pages = Pages(archive);
        if (page < 1 || page > pages.Count) throw new ArgumentOutOfRangeException(nameof(page));
        var entry = pages[page - 1];
        if (entry.Length > MaxPageBytes) throw new InvalidDataException("Page exceeds the 40 MB limit.");
        await using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, cancel)) > 0)
        {
            if (output.Length + count > MaxPageBytes) throw new InvalidDataException("Page exceeds the 40 MB limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancel);
        }
        var mime = Path.GetExtension(entry.FullName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif",
            ".avif" => "image/avif", _ => "image/webp"
        };
        return (output.ToArray(), mime);
    }
}

public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        var a = Regex.Split(x ?? "", "([0-9]+)");
        var b = Regex.Split(y ?? "", "([0-9]+)");
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int result;
            if (i % 2 == 1)
            {
                var left = a[i].TrimStart('0'); var right = b[i].TrimStart('0');
                result = left.Length.CompareTo(right.Length);
                if (result == 0) result = string.CompareOrdinal(left, right);
            }
            else result = StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
            if (result != 0) return result;
        }
        var length = a.Length.CompareTo(b.Length);
        return length != 0 ? length : StringComparer.Ordinal.Compare(x, y);
    }
}
