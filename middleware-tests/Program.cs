using System.Net;
using Jellyfin.Plugin.MangaReader;
using Microsoft.Extensions.FileProviders;

var directory = Path.Combine(Path.GetTempPath(), "manga-shell-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "<html><body>Jellyfin</body></html>");
await File.WriteAllTextAsync(Path.Combine(directory, "other.html"), "<body>Other</body>");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddResponseCompression();
builder.Logging.ClearProviders();
await using var app = builder.Build();
app.UseMiddleware<ReaderShellMiddleware>();
app.Map("/jellyfin", inner => {
    inner.UseResponseCompression();
    inner.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(directory), RequestPath = "/web" });
    inner.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(directory), RequestPath = "/web" });
});
try {
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    foreach (var path in new[] { "/jellyfin/web/index.html", "/jellyfin/web/" }) {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == HttpStatusCode.OK, "conditional request remains a full shell");
        Check(html.Contains("src=\"/jellyfin/MangaReader/assets/bridge.js"), "base path preserved");
        Check(html.Split("bridge.js").Length == 2, "single injection");
        Check(response.Content.Headers.ContentLength == System.Text.Encoding.UTF8.GetByteCount(html), "content length correct");
        Check(response.Headers.CacheControl?.NoStore == true && response.Headers.ETag is null, "modified shell cannot be cached");
    }
    Check(!(await client.GetStringAsync("/jellyfin/web/other.html")).Contains("bridge.js"), "other assets untouched");
    Plugin.Instance.Configuration.EnableInAppReader = false;
    Check(!(await client.GetStringAsync("/jellyfin/web/index.html")).Contains("bridge.js"), "disable integration");
    Console.WriteLine("PASS middleware: actual Kestrel, static files, compression, conditional requests, base path, disabled integration");
} finally { await app.StopAsync(); Directory.Delete(directory, true); }
static void Check(bool passed, string name) { if (!passed) throw new Exception(name); }
namespace Jellyfin.Plugin.MangaReader {
    public sealed class Configuration { public bool EnableInAppReader { get; set; } = true; }
    public sealed class Plugin { public static Plugin Instance { get; } = new(); public Configuration Configuration { get; } = new(); }
}
