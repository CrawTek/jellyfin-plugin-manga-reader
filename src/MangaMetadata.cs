using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MangaReader;

public sealed record MangaDetails(int Id, string Title, string[] Titles, string Synopsis, double? Score, string[] Genres, string Status, string? Image);
public sealed record MangaMatch(DateTimeOffset Checked, bool Pinned, MangaDetails? Details);

// Shared server cache. Only administrators can change a title's selected match.
public sealed class MangaMetadata
{
    private readonly string root;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;
    public MangaMetadata(IApplicationPaths paths) : this(Path.Combine(paths.DataPath, "manga-reader", "metadata"),
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }) { }
    public MangaMetadata(string directory, HttpClient client) { root = directory; http = client; }
    private string FilePath(Guid folder) => Path.Combine(root, folder.ToString("N") + ".json");
    public static string Normalize(string title) => string.Concat(title.Normalize(NormalizationForm.FormKD)
        .Where(c => char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
    public static MangaDetails? ExactMatch(string title, IEnumerable<MangaDetails> candidates)
    {
        var matches = candidates.Where(c => c.Titles.Append(c.Title).Any(t => Normalize(t) == Normalize(title))).DistinctBy(c => c.Id).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static bool SafeImage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "cdn.myanimelist.net" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
    private MangaMatch? Read(Guid folder)
    {
        try { return JsonSerializer.Deserialize<MangaMatch>(File.ReadAllText(FilePath(folder))); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    private void Write(Guid folder, MangaMatch value)
    {
        Directory.CreateDirectory(root);
        var path = FilePath(folder); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value)); File.Move(path + ".tmp", path, true);
    }
    private async Task<JsonDocument> Fetch(string path, CancellationToken ct)
    {
        var delay = nextRequest - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1100);
        using var response = await http.GetAsync("https://api.jikan.moe/v4/" + path, ct);
        if ((int)response.StatusCode == 429) nextRequest = DateTimeOffset.UtcNow.AddMinutes(1);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public static MangaDetails Parse(JsonElement item)
    {
        static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        var titles = item.TryGetProperty("titles", out var ts) && ts.ValueKind == JsonValueKind.Array ? ts.EnumerateArray().Select(t => Text(t, "title")).Where(t => t.Length > 0).ToArray() : [];
        var genres = new List<string>();
        foreach (var key in new[] { "genres", "themes", "demographics" })
            if (item.TryGetProperty(key, out var gs) && gs.ValueKind == JsonValueKind.Array) genres.AddRange(gs.EnumerateArray().Select(g => Text(g, "name")));
        string? image = null;
        if (item.TryGetProperty("images", out var images) && images.TryGetProperty("jpg", out var jpg)) image = Text(jpg, "large_image_url");
        return new(item.GetProperty("mal_id").GetInt32(), Text(item, "title"), titles, Text(item, "synopsis"),
            item.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var number) && number > 0 ? number : null,
            genres.Distinct().ToArray(), Text(item, "status"), SafeImage(image) ? image : null);
    }
    private async Task<MangaDetails[]> SearchCore(string title, CancellationToken ct)
    {
        using var doc = await Fetch("manga?limit=15&q=" + Uri.EscapeDataString(title), ct);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(Parse).ToArray();
    }
    public async Task<MangaDetails[]> Search(string title, CancellationToken ct)
    {
        await gate.WaitAsync(ct); try { return await SearchCore(title, ct); } finally { gate.Release(); }
    }
    public async Task<MangaMatch> Get(Guid folder, string title, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var cached = Read(folder);
            if (cached is not null && DateTimeOffset.UtcNow - cached.Checked < TimeSpan.FromDays(cached.Details is null ? 1 : 7)) return cached;
            try
            {
                MangaDetails? details;
                if (cached?.Details is { } previous)
                { using var doc = await Fetch("manga/" + previous.Id, ct); details = Parse(doc.RootElement.GetProperty("data")); }
                else details = ExactMatch(title, await SearchCore(title, ct));
                var value = new MangaMatch(DateTimeOffset.UtcNow, cached?.Pinned ?? false, details); Write(folder, value); return value;
            }
            catch (Exception e) when (cached is not null && e is HttpRequestException or TaskCanceledException or JsonException)
            { return cached; }
        }
        finally { gate.Release(); }
    }
    public async Task<MangaMatch> Identify(Guid folder, int id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var doc = await Fetch("manga/" + id, ct);
            var value = new MangaMatch(DateTimeOffset.UtcNow, true, Parse(doc.RootElement.GetProperty("data")));
            Write(folder, value); return value;
        }
        finally { gate.Release(); }
    }
    public async Task<byte[]?> Cover(Guid folder, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var details = Read(folder)?.Details;
            if (details is null || !SafeImage(details.Image)) return null;
            var path = Path.Combine(root, details.Id + ".jpg");
            if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
            using var response = await http.GetAsync(details.Image, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new IOException("Cover too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            { if (output.Length + count > 8 * 1024 * 1024) throw new IOException("Cover too large."); output.Write(buffer, 0, count); }
            var bytes = output.ToArray();
            if (bytes.Length < 3 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[2] != 0xff) throw new IOException("Invalid JPEG cover.");
            Directory.CreateDirectory(root); await File.WriteAllBytesAsync(path + ".tmp", bytes, ct); File.Move(path + ".tmp", path, true); return bytes;
        }
        finally { gate.Release(); }
    }
}
