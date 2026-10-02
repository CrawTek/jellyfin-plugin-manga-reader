using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MangaReader;

public sealed record MangaFolder(Guid Id, string Name)
{
    // Jellyfin's logical parent can be shared by books in different disk folders.
    // Use the physical containing directory for both shelf grouping and metadata.
    public static MangaFolder ForBook(string bookPath)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(bookPath)!));
        var name = Path.GetFileName(directory);
        var identity = OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("manga-reader:folder:v1:" + identity));
        return new(new Guid(hash.AsSpan(0, 16)), name);
    }
}
