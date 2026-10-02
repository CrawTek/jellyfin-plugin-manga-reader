namespace Jellyfin.Plugin.MangaReader;

public static class MangaFolderStorage
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, Comparison);
    public static string? RegisteredDirectory(string book, IEnumerable<string> roots)
    {
        var directory = Path.GetFullPath(Path.GetDirectoryName(book)!);
        foreach (var root in roots.Select(Path.GetFullPath))
        {
            if (!Within(directory, root) && !string.Equals(directory, Path.TrimEndingDirectorySeparator(root), Comparison)) continue;
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked manga folders cannot be modified.");
            return directory;
        }
        return null;
    }
    public static string TargetName(MangaDetails details, string mode)
    {
        if (mode is not ("english" or "id")) throw new IOException("Choose English title or MAL ID.");
        var name = mode == "english" ? details.EnglishTitle ?? "" : "";
        name = string.Concat(name.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c))).Trim().TrimEnd('.', ' ');
        if (name.Length > 120) name = name[..120].TrimEnd('.', ' ');
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (name.Length == 0 || stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = details.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return name;
    }
    public static string Destination(string source, string name, IEnumerable<string> roots)
    {
        var locations = roots.ToArray();
        if (RegisteredDirectory(Path.Combine(source, "chapter.cbz"), locations) is null ||
            locations.Any(r => !Within(source, r) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)), source, Comparison)) ||
            locations.Any(r => Within(r, source))) throw new IOException("A library root cannot be renamed.");
        if (name != Path.GetFileName(name) || name is "" or "." or "..") throw new IOException("Invalid folder name.");
        var destination = Path.Combine(Path.GetDirectoryName(source)!, name);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("That folder name already exists. No folders were combined or overwritten.");
        return destination;
    }
}
