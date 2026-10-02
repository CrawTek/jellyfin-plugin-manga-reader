using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MangaReader;

[ApiController]
[Authorize]
[Route("MangaReader")]
public sealed class ReaderController(ILibraryManager library, IUserManager users, ProgressStore progress, ArchiveReader archives, MangaMetadata metadata) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;
    private static readonly object ConfigurationLock = new();
    private static readonly SemaphoreSlim RenameLock = new(1, 1);
    private string[] MangaRoots => library.GetVirtualFolders().Where(f => Guid.TryParse(f.ItemId, out var id) && MangaLibraryIds.Contains(id)).SelectMany(f => f.Locations).ToArray();
    private string? IdentityDirectory(Book book) => MangaFolderStorage.RegisteredDirectory(book.Path, MangaRoots);
    public sealed record MalSettings(string? ClientId);
    private static bool ValidClientId(string value) => value.Length <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpGet("settings/mal")]
    public IActionResult MalConfiguration() => Ok(new { configured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.MalClientId) });

    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpPut("settings/mal")]
    public IActionResult SaveMalConfiguration(MalSettings settings)
    {
        var value = settings.ClientId?.Trim();
        if (value is null || !ValidClientId(value)) return BadRequest("Enter the MAL Client ID, not a client secret or access token.");
        lock (ConfigurationLock)
        {
            var plugin = Plugin.Instance!; plugin.Configuration.MalClientId = value; plugin.SaveConfiguration();
        }
        return Ok(new { configured = value.Length > 0 });
    }
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpPost("settings/mal/test")]
    public async Task<IActionResult> TestMalConfiguration(MalSettings settings, CancellationToken ct)
    {
        var value = string.IsNullOrWhiteSpace(settings.ClientId) ? Plugin.Instance?.Configuration.MalClientId ?? "" : settings.ClientId.Trim();
        if (!ValidClientId(value)) return BadRequest("Enter a valid MAL Client ID.");
        try { await metadata.TestConnection(value, ct); return Ok(new { connected = true }); }
        catch (MangaProviderException e) { return Problem(e.Message, statusCode: 503); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        { return Problem("Could not connect to the official MyAnimeList API. Try again later.", statusCode: 503); }
    }
    private static Guid[] MangaLibraryIds => (Plugin.Instance?.Configuration.MangaLibraryIds ?? [])
        .Append(Plugin.Instance?.Configuration.MangaLibraryId ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray();

    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpPost("libraries/{id:guid}")]
    public IActionResult RegisterLibrary(Guid id)
    {
        if (!library.GetVirtualFolders().Any(f => Guid.TryParse(f.ItemId, out var folderId) && folderId == id &&
            string.Equals(f.CollectionType?.ToString(), "books", StringComparison.OrdinalIgnoreCase)))
            return BadRequest("Choose an existing Books library.");
        lock (ConfigurationLock)
        {
            var plugin = Plugin.Instance!;
            plugin.Configuration.MangaLibraryIds = MangaLibraryIds.Append(id).Distinct().ToArray();
            plugin.SaveConfiguration();
        }
        return NoContent();
    }

    [HttpGet("bootstrap")]
    public IActionResult Bootstrap()
    {
        if (UserId == Guid.Empty) return Unauthorized();
        var user = users.GetUserById(UserId);
        if (user is null) return Unauthorized();
        var items = MangaLibraryIds.Select(id => library.GetItemById<BaseItem>(id, user)).Where(item => item is not null && item.IsVisible(user));
        return Ok(new { libraries = items.Select(item => new { id = item!.Id, name = item.Name }), enabled = Plugin.Instance?.Configuration.EnableInAppReader == true });
    }

    [HttpGet("library")]
    public IActionResult Library([FromQuery] int start = 0, [FromQuery] Guid libraryId = default)
    {
        if (UserId == Guid.Empty) return Unauthorized();
        var user = users.GetUserById(UserId);
        if (user is null) return Unauthorized();
        if (start < 0) return BadRequest();
        if (libraryId != Guid.Empty && (!MangaLibraryIds.Contains(libraryId) || library.GetItemById<BaseItem>(libraryId, user) is null)) return NotFound();
        var items = library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Book],
            Recursive = true, ParentId = libraryId, StartIndex = start, Limit = 100,
            OrderBy = [(Jellyfin.Data.Enums.ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)]
        });
        return Ok(new
        {
            canIdentify = User.IsInRole("Administrator"),
            next = items.Count == 100 ? (int?)(start + 100) : null,
            items = items.OfType<Book>().Where(b => b.IsVisible(user) && ArchiveReader.Supports(b.Path))
                .Select(b => { var folder = MangaFolder.ForBook(b.Path); return new { id = b.Id, title = b.Name, seriesId = folder.Id, series = folder.Name, progress = progress.Get(UserId, b.Id) }; }).ToArray()
        });
    }
    private Book? AccessibleBook(Guid id)
    {
        if (UserId == Guid.Empty) return null;
        var user = users.GetUserById(UserId);
        if (user is null) return null;
        var item = library.GetItemById<Book>(id, user);
        return item is not null && item.IsVisible(user) && ArchiveReader.Supports(item.Path) ? item : null;
    }

    [HttpGet("books/{id:guid}/metadata")]
    public async Task<IActionResult> Metadata(Guid id, CancellationToken ct)
    {
        var book = AccessibleBook(id); if (book is null) return NotFound();
        var folder = MangaFolder.ForBook(book.Path);
        try { return Ok(await metadata.Get(folder.Id, folder.Name, ct, IdentityDirectory(book))); }
        catch (MangaProviderException e) { return Problem(e.Message, statusCode: 503); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        { return Problem("MyAnimeList metadata is temporarily unavailable. You can still read your manga.", statusCode: 503); }
    }
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpGet("books/{id:guid}/matches")]
    public async Task<IActionResult> Matches(Guid id, [FromQuery] string query, CancellationToken ct)
    {
        if (AccessibleBook(id) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500) return BadRequest();
        try { return Ok(await metadata.Search(query, ct)); }
        catch (MangaProviderException e) { return Problem(e.Message, statusCode: 503); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        { return Problem("The official MyAnimeList API is temporarily unavailable. Try again later. Your manga files and reading progress are unaffected.", statusCode: 503); }
    }
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpPut("books/{id:guid}/metadata/{malId:int}")]
    public async Task<IActionResult> Identify(Guid id, int malId, CancellationToken ct)
    {
        var book = AccessibleBook(id); if (book is null) return NotFound();
        if (malId < 1) return BadRequest();
        try { return Ok(await metadata.Identify(MangaFolder.ForBook(book.Path).Id, malId, ct, IdentityDirectory(book))); }
        catch (MangaProviderException e) { return Problem(e.Message, statusCode: 503); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        { return Problem("Could not save this manga match. Try again shortly.", statusCode: 503); }
    }
    [HttpGet("books/{id:guid}/cover")]
    public async Task<IActionResult> Cover(Guid id, CancellationToken ct)
    {
        var book = AccessibleBook(id); if (book is null) return NotFound();
        try
        {
            var bytes = await metadata.Cover(MangaFolder.ForBook(book.Path).Id, ct); if (bytes is null) return NotFound();
            Response.Headers["Cache-Control"] = "private, no-store"; return File(bytes, "image/jpeg");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        { return NotFound(); }
    }

    public sealed record RenameRequest(string Mode, string? ExpectedName, int MalId);
    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpPost("books/{id:guid}/rename-preview")]
    public Task<IActionResult> PreviewRename(Guid id, RenameRequest value, CancellationToken ct) => RenameFolder(id, value, false, ct);

    [Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
    [HttpPost("books/{id:guid}/rename")]
    public Task<IActionResult> ApplyRename(Guid id, RenameRequest value, CancellationToken ct) => RenameFolder(id, value, true, ct);

    private async Task<IActionResult> RenameFolder(Guid id, RenameRequest value, bool apply, CancellationToken ct)
    {
        await RenameLock.WaitAsync(ct);
        try
        {
            var book = AccessibleBook(id); if (book is null) return NotFound();
            var source = IdentityDirectory(book);
            if (source is null) return Problem("Enable this library in Manga Reader settings first.", statusCode: 422);
            var folder = MangaFolder.ForBook(book.Path);
            var details = (await metadata.Get(folder.Id, folder.Name, ct, source)).Details;
            if (details is null) return Problem("Identify this manga before renaming its folder.", statusCode: 422);
            var name = MangaFolderStorage.TargetName(details, value.Mode);
            var destination = MangaFolderStorage.Destination(source, name, MangaRoots);
            if (!apply) return Ok(new { sourceName = Path.GetFileName(source), targetName = name, malId = details.Id });
            if (value.ExpectedName != name || value.MalId != details.Id) return Problem("The manga identification changed. Preview the rename again.", statusCode: 422);
            var items = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Book], Recursive = true });
            var mapping = items.OfType<Book>().Where(b => !string.IsNullOrEmpty(b.Path) && MangaFolderStorage.Within(b.Path, source)).ToDictionary(b => b.Id,
                b => library.GetNewItemId(Path.Combine(destination, Path.GetRelativePath(source, b.Path)), b.GetType()));
            MangaIdentityFile.Write(source, details);
            progress.CopyForRename(mapping, () => Directory.Move(source, destination));
            library.QueueLibraryScan();
            return Ok(new { renamed = true, targetName = name });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MangaProviderException)
        { return Problem(e is UnauthorizedAccessException ? "Give Jellyfin write access to the manga folder and its parent before renaming." : e.Message, statusCode: 422); }
        finally { RenameLock.Release(); }
    }

    [AllowAnonymous]
    [HttpGet("reader")]
    public IActionResult Reader() => Asset("index.html", "text/html; charset=utf-8");

    [AllowAnonymous]
    [HttpGet("assets/{name}")]
    public IActionResult Assets(string name) => name switch
    {
        "reader.js" => Asset(name, "text/javascript; charset=utf-8"),
        "reader.css" => Asset(name, "text/css; charset=utf-8"),
        "bridge.js" => Asset(name, "text/javascript; charset=utf-8"),
        "bridge.css" => Asset(name, "text/css; charset=utf-8"),
        "config.js" => Asset(name, "text/javascript; charset=utf-8"),
        _ => NotFound()
    };

    private IActionResult Asset(string name, string mime)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'self'; base-uri 'none'; form-action 'self'";
        return File(typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.MangaReader.Web." + name)!, mime);
    }

    [HttpGet("books/{id:guid}")]
    public IActionResult BookInfo(Guid id)
    {
        var book = AccessibleBook(id);
        if (book is null) return NotFound();
        try { return Ok(new { id, title = book.Name, total = archives.Count(book.Path), progress = progress.Get(UserId, id) }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Problem("This archive is unavailable or invalid.", statusCode: 422); }
    }

    [HttpGet("books/{id:guid}/pages/{page:int}")]
    public async Task<IActionResult> Page(Guid id, int page, CancellationToken cancellationToken)
    {
        var book = AccessibleBook(id);
        if (book is null) return NotFound();
        try
        {
            var image = await archives.Read(book.Path, page, cancellationToken);
            Response.Headers["Cache-Control"] = "private, no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(image.Data, image.Mime);
        }
        catch (ArgumentOutOfRangeException) { return NotFound(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Problem("This page could not be read.", statusCode: 422); }
    }

    [HttpPut("books/{id:guid}/progress")]
    public IActionResult Save(Guid id, SaveProgress value)
    {
        var book = AccessibleBook(id);
        if (book is null) return NotFound();
        try { return Ok(progress.Save(UserId, id, value, archives.Count(book.Path))); }
        catch (ProgressConflictException) { return Conflict(new { message = "Progress changed in another reader. Reopen this book to resume.", progress = progress.Get(UserId, id) }); }
        catch (ArgumentException) { return BadRequest("Invalid page or direction."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Problem("Progress could not be saved. Check the server storage and archive.", statusCode: 422); }
    }
}
