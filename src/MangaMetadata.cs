using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MangaReader;

public sealed record MangaDetails(int Id, string Title, string[] Titles, string Synopsis, double? Score, string[] Genres, string Status, string? Image, string? EnglishTitle = null);
public sealed record MangaMatch(DateTimeOffset Checked, bool Pinned, MangaDetails? Details, string? Notice = null);
public sealed class MangaProviderException(string message) : Exception(message);

// Shared server cache. Only administrators can change a title's selected match.
public sealed class MangaMetadata
{
    private readonly string root;
    private readonly HttpClient http;
    private readonly Func<string> clientId;
    private const string Fields = "id,title,main_picture,alternative_titles,synopsis,mean,status,genres";
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;
    public MangaMetadata(IApplicationPaths paths, Func<string> getClientId) : this(Path.Combine(paths.DataPath, "manga-reader", "metadata"),
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }, getClientId) { }
    public MangaMetadata(string directory, HttpClient client, Func<string> getClientId) { root = directory; http = client; clientId = getClientId; }
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
    private async Task<JsonDocument> Fetch(string path, CancellationToken ct, string? candidateClientId = null)
    {
        var key = (candidateClientId ?? clientId()).Trim();
        if (string.IsNullOrEmpty(key)) throw new MangaProviderException("Add a MyAnimeList API Client ID in Dashboard → Plugins → Manga Reader → Settings to enable metadata.");
        var delay = nextRequest - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1100);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.myanimelist.net/v2/" + path);
        request.Headers.Add("X-MAL-CLIENT-ID", key);
        request.Headers.UserAgent.ParseAdd("Jellyfin-MangaReader/0.5.0");
        using var response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode is 401 or 403) throw new MangaProviderException("MyAnimeList rejected the request. Check the API Client ID in Manga Reader settings and that your MAL application is enabled.");
        if ((int)response.StatusCode == 429) { nextRequest = DateTimeOffset.UtcNow.AddMinutes(1); throw new MangaProviderException("MyAnimeList is limiting requests. Wait a minute before trying again."); }
        if ((int)response.StatusCode == 404) throw new MangaProviderException("MyAnimeList could not find that manga ID. Check that the link is for a manga, not an anime.");
        if ((int)response.StatusCode == 400) throw new MangaProviderException("MyAnimeList could not accept that search. Try a title of at least two characters or a manga ID.");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public static MangaDetails Parse(JsonElement item)
    {
        static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        var titles = new List<string>(); string? englishTitle = null;
        if (item.TryGetProperty("alternative_titles", out var alternatives) && alternatives.ValueKind == JsonValueKind.Object)
        {
            titles.Add(Text(alternatives, "en")); titles.Add(Text(alternatives, "ja"));
            englishTitle = Text(alternatives, "en");
            if (alternatives.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                titles.AddRange(synonyms.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!));
        }
        var genres = new List<string>();
        foreach (var key in new[] { "genres" })
            if (item.TryGetProperty(key, out var gs) && gs.ValueKind == JsonValueKind.Array) genres.AddRange(gs.EnumerateArray().Select(g => Text(g, "name")));
        string? image = null;
        if (item.TryGetProperty("main_picture", out var picture) && picture.ValueKind == JsonValueKind.Object)
        { image = Text(picture, "large"); if (string.IsNullOrEmpty(image)) image = Text(picture, "medium"); }
        return new(item.GetProperty("id").GetInt32(), Text(item, "title"), titles.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray(), Text(item, "synopsis"),
            item.TryGetProperty("mean", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var number) && number > 0 ? number : null,
            genres.Distinct().ToArray(), Text(item, "status").Replace('_', ' '), SafeImage(image) ? image : null, string.IsNullOrWhiteSpace(englishTitle) ? null : englishTitle);
    }
    private async Task<MangaDetails[]> SearchCore(string title, CancellationToken ct)
    {
        using var doc = await Fetch("manga?limit=15&nsfw=true&fields=" + Fields + "&q=" + Uri.EscapeDataString(title), ct);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(entry => Parse(entry.GetProperty("node"))).ToArray();
    }
    public static int? MangaId(string value)
    {
        value = value.Trim();
        if (int.TryParse(value, out var id) && id > 0) return id;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host is "myanimelist.net" or "www.myanimelist.net" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0] == "manga" && int.TryParse(segments[1], out id) && id > 0) return id;
        }
        return null;
    }
    public async Task<MangaDetails[]> Search(string title, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (MangaId(title) is { } id)
            { using var doc = await Fetch("manga/" + id + "?fields=" + Fields, ct); return [Parse(doc.RootElement)]; }
            return await SearchCore(title, ct);
        }
        finally { gate.Release(); }
    }
    private static MangaMatch PersistIdentity(MangaMatch value, string? directory)
    {
        if (directory is null || value.Details is null) return value;
        try
        {
            var previous = MangaIdentityFile.Read(directory);
            if (previous is null || previous.MalId != value.Details.Id || previous.EnglishTitle != value.Details.EnglishTitle || previous.Title != value.Details.Title)
                MangaIdentityFile.Write(directory, value.Details);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return value with { Notice = "Metadata loaded, but manga-reader.json could not be saved. Give Jellyfin write access to this manga folder to preserve identification after a server reset." }; }
    }
    public async Task<MangaMatch> Get(Guid folder, string title, CancellationToken ct, string? directory = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var cached = Read(folder);
            MangaIdentity? identity;
            try { identity = MangaIdentityFile.Read(directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { throw new MangaProviderException("The manga-reader.json identification file cannot be read. Check its format and folder permissions before identifying this manga."); }
            if (identity is not null && cached?.Details?.Id != identity.MalId)
                cached = new(DateTimeOffset.MinValue, true, new(identity.MalId, identity.Title, [], "", null, [], "", null, identity.EnglishTitle));
            if (cached is not null && DateTimeOffset.UtcNow - cached.Checked < TimeSpan.FromDays(cached.Details is null ? 1 : 7)) return PersistIdentity(cached, directory);
            try
            {
                MangaDetails? details;
                if (cached?.Details is { } previous)
                { using var doc = await Fetch("manga/" + previous.Id + "?fields=" + Fields, ct); details = Parse(doc.RootElement); }
                else details = ExactMatch(title, await SearchCore(title, ct));
                var value = new MangaMatch(DateTimeOffset.UtcNow, cached?.Pinned ?? false, details); Write(folder, value); return PersistIdentity(value, directory);
            }
            catch (Exception e) when (cached is not null && e is HttpRequestException or TaskCanceledException or JsonException or MangaProviderException)
            { return cached with { Notice = "Showing saved identification. MyAnimeList metadata could not be refreshed; check the Client ID and connection in Manga Reader settings." }; }
        }
        finally { gate.Release(); }
    }
    public async Task<MangaMatch> Identify(Guid folder, int id, CancellationToken ct, string? directory = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var doc = await Fetch("manga/" + id + "?fields=" + Fields, ct);
            var value = new MangaMatch(DateTimeOffset.UtcNow, true, Parse(doc.RootElement));
            // Explicit identification replaces an older ID stored alongside the manga.
            if (directory is not null)
            {
                try { MangaIdentityFile.Write(directory, value.Details!); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { throw new MangaProviderException("Could not save manga-reader.json. Give Jellyfin write access to this manga folder and identify it again."); }
            }
            Write(folder, value); return value;
        }
        finally { gate.Release(); }
    }
    public async Task TestConnection(string candidateClientId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { using var doc = await Fetch("manga/2?fields=id,title", ct, candidateClientId); doc.RootElement.GetProperty("id").GetInt32(); }
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
