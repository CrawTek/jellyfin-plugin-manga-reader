using System.Text.Json;

namespace Jellyfin.Plugin.MangaReader;

public sealed record MangaIdentity(int Version, int MalId, string Title, string? EnglishTitle);

public static class MangaIdentityFile
{
    public const string Name = "manga-reader.json";
    public static MangaIdentity? Read(string? directory)
    {
        if (directory is null) return null;
        var path = Path.Combine(directory, Name);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > 65536 || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid manga identification file.");
        var value = JsonSerializer.Deserialize<MangaIdentity>(File.ReadAllText(path));
        if (value is null || value.Version != 1 || value.MalId < 1 || string.IsNullOrWhiteSpace(value.Title)) throw new IOException("Invalid manga identification file.");
        return value;
    }
    public static void Write(string directory, MangaDetails details)
    {
        var path = Path.Combine(directory, Name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Identification file cannot be a symbolic link.");
        var temporary = Path.Combine(directory, ".manga-reader-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new MangaIdentity(1, details.Id, details.Title, details.EnglishTitle), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
