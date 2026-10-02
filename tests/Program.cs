using System.IO.Compression;
using Jellyfin.Plugin.MangaReader;

var root = Path.Combine(Path.GetTempPath(), "manga-reader-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Throws<T>(Action action, string name) where T : Exception
{ try { action(); } catch (T) { Check(true, name); return; } throw new Exception(name); }
try
{
    var path = Path.Combine(root, "book.cbz");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        foreach (var name in new[] { "10.png", "2.png", "1.png", "__MACOSX/._cover.png", "ComicInfo.xml" })
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(name); }
    var reader = new ArchiveReader();
    Check(reader.Count(path) == 3, "Ignore metadata and hidden archive images");
    Check(System.Text.Encoding.UTF8.GetString((await reader.Read(path, 2, default)).Data) == "2.png", "Natural order is 1, 2, 10");
    Check((await reader.Read(path, 1, default)).Mime == "image/png", "Page MIME type");
    Throws<ArgumentOutOfRangeException>(() => reader.Read(path, 0, default).GetAwaiter().GetResult(), "Reject page zero");
    Throws<ArgumentOutOfRangeException>(() => reader.Read(path, 4, default).GetAwaiter().GetResult(), "Reject page after end");
    var oversized = Path.Combine(root, "oversized.cbz");
    using (var zip = ZipFile.Open(oversized, ZipArchiveMode.Create))
    { using var stream = zip.CreateEntry("1.png").Open(); var block = new byte[1024*1024]; for (int i=0;i<41;i++) stream.Write(block); }
    Throws<InvalidDataException>(() => reader.Read(oversized, 1, default).GetAwaiter().GetResult(), "Reject oversized decompressed page");
    var empty = Path.Combine(root, "empty.cbz");
    using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create)) zip.CreateEntry("notes.txt");
    Throws<InvalidDataException>(() => reader.Count(empty), "Reject archives with no image pages");
    var a = Guid.NewGuid(); var b = Guid.NewGuid(); var book = Guid.NewGuid();
    var store = new ProgressStore(root);
    Check(store.Get(a, book) is null, "New reader has no progress");
    var saved = store.Save(a, book, new(2, "rtl", 0), 3);
    Check(saved.Page == 2 && saved.Revision == 1, "Save exact current page");
    Check(new ProgressStore(root).Get(a, book)?.Page == 2, "Resume after store restart");
    Check(store.Get(b, book) is null, "Progress isolated per user");
    Throws<ProgressConflictException>(() => store.Save(a, book, new(1, "ltr", 0), 3), "Stale device cannot overwrite newer progress");
    Check(store.Get(a, book)?.Page == 2, "Conflict preserves saved page");
    Throws<ArgumentException>(() => store.Save(a, book, new(4, "rtl", 1), 3), "Reject invalid saved page");
    Throws<ArgumentException>(() => store.Save(a, book, new(1, "invalid", 1), 3), "Reject invalid direction");
    store.Save(a, book, new(1, "ltr", 1), 3);
    Check(store.Get(a, book)?.Page == 1, "Re-reading an earlier page updates resume");
    using var data = System.Text.Json.JsonDocument.Parse("""{"mal_id":1,"title":"Paper Moon","titles":[{"title":"The Paper Moon"}],"score":null,"synopsis":"Example","genres":[{"name":"Adventure"}],"images":{"jpg":{"large_image_url":"https://cdn.myanimelist.net/images/manga/1/test.jpg"}}}""");
    var details = MangaMetadata.Parse(data.RootElement);
    Check(details.Score is null && details.Genres.SequenceEqual(new[]{"Adventure"}), "Missing score remains unrated; genres parsed");
    Check(MangaMetadata.ExactMatch("The Paper Moon", [details])?.Id == 1, "Alternate title exact match");
    Check(MangaMetadata.ExactMatch("The Paper", [details]) is null, "Do not guess a partial title");
    Check(MangaMetadata.ExactMatch("Paper Moon", [details, details with { Id = 2 }]) is null, "Ambiguous matches require identification");
    Check(MangaMetadata.SafeImage(details.Image) && !MangaMetadata.SafeImage("https://cdn.myanimelist.net.evil.test/a.jpg") && !MangaMetadata.SafeImage("http://127.0.0.1/a.jpg"), "Restrict cover fetches to HTTPS MAL CDN");
    var handler = new MetadataHandler(data.RootElement.GetRawText());
    using var client = new HttpClient(handler);
    var metaRoot = Path.Combine(root, "metadata");
    var meta = new MangaMetadata(metaRoot, client);
    Check((await meta.Get(a, "The Paper Moon", default)).Details?.Id == 1, "Fetch and cache matched manga");
    Check((await new MangaMetadata(metaRoot, client).Get(a, "The Paper Moon", default)).Details?.Id == 1 && handler.Calls == 1, "Metadata survives restart without another request");
    Check((await meta.Identify(b, 1, default)).Pinned, "Administrator identification persists selected match");
    handler.Fail = true;
    var cacheFile = Path.Combine(metaRoot, a.ToString("N") + ".json");
    File.WriteAllText(cacheFile, System.Text.Json.JsonSerializer.Serialize(new MangaMatch(DateTimeOffset.UtcNow.AddDays(-8), false, details)));
    Check((await meta.Get(a, "The Paper Moon", default)).Details?.Id == 1, "Provider outage retains cached metadata");
    Console.WriteLine($"{checks} checks passed.");
}
finally { Directory.Delete(root, true); }

// Compile core logic independently of the server; the production constructor uses the real interface.
namespace MediaBrowser.Common.Configuration { public interface IApplicationPaths { string DataPath { get; } } }

sealed class MetadataHandler(string item) : HttpMessageHandler
{
    public int Calls;
    public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(Fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":" + (request.RequestUri!.Query.Length > 0 ? "[" + item + "]" : item) + "}") });
    }
}
