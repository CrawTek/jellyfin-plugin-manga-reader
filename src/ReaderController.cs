using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MangaReader;

[ApiController]
[Authorize]
[Route("MangaReader")]
public sealed class ReaderController(ILibraryManager library, IUserManager users, ProgressStore progress, ArchiveReader archives) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    [HttpGet("library")]
    public IActionResult Library([FromQuery] int start = 0)
    {
        if (UserId == Guid.Empty) return Unauthorized();
        var user = users.GetUserById(UserId);
        if (user is null) return Unauthorized();
        if (start < 0) return BadRequest();
        var items = library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Book],
            Recursive = true, StartIndex = start, Limit = 100,
            OrderBy = [(Jellyfin.Data.Enums.ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)]
        });
        return Ok(new
        {
            next = items.Count == 100 ? (int?)(start + 100) : null,
            items = items.OfType<Book>().Where(b => b.IsVisible(user) && ArchiveReader.Supports(b.Path))
                .Select(b => new { id = b.Id, title = b.Name, progress = progress.Get(UserId, b.Id) }).ToArray()
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

    [AllowAnonymous]
    [HttpGet("reader")]
    public IActionResult Reader() => Asset("index.html", "text/html; charset=utf-8");

    [AllowAnonymous]
    [HttpGet("assets/{name}")]
    public IActionResult Assets(string name) => name switch
    {
        "reader.js" => Asset(name, "text/javascript; charset=utf-8"),
        "reader.css" => Asset(name, "text/css; charset=utf-8"),
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
