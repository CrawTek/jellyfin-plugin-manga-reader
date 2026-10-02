using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MangaReader;

public sealed record ReadingProgress(int Page, int Total, string Direction, DateTimeOffset UpdatedAt, long Revision);
public sealed record SaveProgress(int Page, string Direction, long Revision);
public sealed class ProgressConflictException : Exception { }

public sealed class ProgressStore
{
    private readonly string root;
    private readonly object gate = new();
    public ProgressStore(IApplicationPaths paths) : this(Path.Combine(paths.DataPath, "manga-reader")) { }
    public ProgressStore(string directory) => root = directory;
    private string FilePath(Guid user, Guid book) => Path.Combine(root, user.ToString("N"), book.ToString("N") + ".json");

    public ReadingProgress? Get(Guid user, Guid book)
    {
        lock (gate)
        {
            var path = FilePath(user, book);
            return File.Exists(path) ? JsonSerializer.Deserialize<ReadingProgress>(File.ReadAllText(path)) : null;
        }
    }

    // Retain the original records for rollback; never replace progress at a destination.
    public void CopyForRename(IReadOnlyDictionary<Guid, Guid> books, Action move)
    {
        lock (gate)
        {
            var copies = new List<(string Source, string Target)>();
            if (Directory.Exists(root))
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var user)) continue;
                    foreach (var pair in books.Where(p => p.Key != p.Value))
                    {
                        var source = FilePath(user, pair.Key); var target = FilePath(user, pair.Value);
                        if (!File.Exists(source)) continue;
                        if (File.Exists(target)) throw new IOException("Reading progress already exists for the destination. Choose another folder name.");
                        copies.Add((source, target));
                    }
                }
            var created = new List<string>();
            try
            {
                foreach (var copy in copies) { File.Copy(copy.Source, copy.Target); created.Add(copy.Target); }
                move();
            }
            catch { foreach (var path in created) File.Delete(path); throw; }
        }
    }

    public ReadingProgress Save(Guid user, Guid book, SaveProgress value, int total)
    {
        if (value.Page < 1 || value.Page > total || value.Direction is not ("rtl" or "ltr"))
            throw new ArgumentException("Invalid page or reading direction.");
        lock (gate)
        {
            var previous = Get(user, book);
            if ((previous?.Revision ?? 0) != value.Revision) throw new ProgressConflictException();
            var progress = new ReadingProgress(value.Page, total, value.Direction, DateTimeOffset.UtcNow, value.Revision + 1);
            var path = FilePath(user, book);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(progress));
            File.Move(temporary, path, true);
            return progress;
        }
    }
}
