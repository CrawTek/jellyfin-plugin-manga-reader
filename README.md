# Manga Reader for Jellyfin

Read local CBZ/ZIP manga inside Jellyfin Web and the Android **phone/tablet** app, with page turning and a separate saved place for every account.

**v0.5 preview · Jellyfin 12.1 · .NET 10**

Live Jellyfin 12.1 checks verified the Manga selector, folder selection, library creation, existing-library registration, embedded reading, page turning and saved-page resume. Physical Android-device testing is still pending.

## Install and add a library

1. In **Dashboard → Plugins → Repositories**, add `https://github.com/CrawTek/jellyfin-plugin-manga-reader/releases/latest/download/manifest.json`.
2. Install **Manga Reader**, restart Jellyfin, and reload the web page. Fully close and reopen the Android app after an update.
3. Open **Dashboard → Libraries → Add Media Library**.
4. Select **Manga** as the content type. Set the display name, use Jellyfin's normal folder picker to select your manga directory, and save.
5. After the scan, select that library on the home screen to open the reader within Jellyfin.

Use a path visible to the Jellyfin process. For Docker, select the mounted container path. A Windows network share address is not necessarily the path Jellyfin sees.

Existing Books libraries can be enabled under **Dashboard → Plugins → Manga Reader**. This does not move or copy files. Multiple manga libraries are supported. If library creation succeeds but registration fails, the plugin tells you to enable the existing library here instead of creating a duplicate.

For manual installation, extract the release ZIP into a MangaReader directory under Jellyfin's plugins directory and restart the server.

## How the integration works

Manga is a preset backed by Jellyfin's existing **Books** type. The plugin adds the Manga option to the standard creation dialog and preserves its folder picker, validation, permissions and scanning. Jellyfin may still display Books in library-management details and other clients.

The plugin inserts a small script into the HTML shell served by Jellyfin, without changing the installed web files. Clicking a registered library opens a same-origin reader inside the current app window. The Android phone/tablet app uses this server-hosted web interface. The reader reuses the signed-in account without a second login or putting a token in a URL. Android's navigation hook and the reader's close button wait for progress saves.

This depends on Jellyfin Web's interface and must be rechecked when upgrading Jellyfin. A separately hosted web client, restrictive custom content-security policy, or another plugin that replaces the web shell may need additional integration. Roku, Android TV and other native TV clients are **not supported**. Physical Android-device validation remains pending; automated tests cover the WebView integration boundaries, not a real Android device.

An optional standalone reader remains available at `<Jellyfin base URL>/MangaReader/reader`. It lists the account's accessible supported Books and keeps its login token only in that browser tab's session storage. The in-app reader keeps its token in memory.

## Reading and saved progress

- Right-to-left or left-to-right controls, touch swipes and arrow keys.
- Jump to a page, fit page/width, fullscreen and search.
- The current page and reading direction save to the server after the image loads.
- Continue-reading cards resume across devices using the same Jellyfin account.
- Failed saves are shown with a retry control. Concurrent readers detect conflicting progress; reopen the book to use the latest saved place.
- Jellyfin authentication and library access rules apply to every book, image and progress request. Registration requires administrator access.

Progress lives in Jellyfin's data directory under `manga-reader/<user-id>/<book-id>.json`; include it in backups. Deleting and re-adding an item can change its ID. Reader progress is separate from Jellyfin video playback and other book readers. A forced app shutdown or lost connection can prevent the last save from reaching the server.

## Formats

CBZ and ZIP archives containing JPEG, PNG, WebP, GIF or AVIF images. Pages use natural filename order (1, 2, 10). Hidden metadata files are ignored. Nothing is extracted alongside the book. Limits: 40 MB per decompressed image and 20,000 archive entries. Image decoding depends on the device's WebView.

CBR/RAR, PDF, EPUB and loose image folders are not supported. ZIP discovery depends on Jellyfin recognizing the file as a Book; CBZ is preferred.

## Build and test

With the .NET 10 SDK and Microsoft Edge installed:

```powershell
dotnet build src -c Release
dotnet run --project tests/CoreTests.csproj -c Release
dotnet run --project middleware-tests/MiddlewareTests.csproj -c Release
npm install
npm test
./scripts/package.ps1 -Repository 'CrawTek/jellyfin-plugin-manga-reader' -Version '0.5.0'
```

The core tests cover archive limits, ordering, progress persistence, user isolation and conflicts. Middleware tests use an actual local ASP.NET server with static files and compression. Browser tests use a mock Jellyfin API and a fixture of the library-creation interface; they check Manga registration, normal Books behavior, folder arguments, embedded login, resume, failed saves, Android navigation and logout. Set `BROWSER_CHANNEL=chrome` to use Chrome instead of Edge.

The release workflow runs these checks, builds the plugin ZIP and publishes its repository manifest with the matching checksum. No developer server address, manga files or credentials are included.

## API

- `GET /MangaReader/bootstrap`: enabled libraries visible to the current user.
- `POST /MangaReader/libraries/{id}`: register an existing Books library (administrator only).
- `GET /MangaReader/library?start=0&libraryId={id}`: supported books and saved progress. Omitting libraryId lists accessible Books for the standalone reader.
- `GET /MangaReader/books/{id}`: title, page count and saved progress.
- `GET /MangaReader/books/{id}/pages/{page}`: one-based image page.
- `PUT /MangaReader/books/{id}/progress`: `{ "page": 12, "direction": "rtl", "revision": 3 }`. Stale revisions return HTTP 409.

Reader assets are public; manga and progress require a user login. Server API keys without a user identity cannot read manga. Page responses disable shared caching.

## License

GNU GPL version 2. See [LICENSE](LICENSE).




Manga titles are grouped by their parent folder name. Open a title to browse its chapters in natural numeric order, then return with All manga. Chapters from separate folders remain separate even when folder names match. Reading progress stays attached to each chapter.


## MyAnimeList metadata

The Manga Reader title shelf displays cover posters and MyAnimeList scores. Open a title for its synopsis, publication status, genres and chapters. The shelf prefers the official English title when available, with the MAL primary title or folder name as a fallback. Metadata is obtained directly from the official MyAnimeList API. In Dashboard → Plugins → Manga Reader → Settings, enter the **Client ID** from your [MyAnimeList API application](https://myanimelist.net/apiconfig), test the connection, and save it. The Client Secret is not needed. Each server owner uses their own Client ID. This does not synchronize a MyAnimeList user's reading list.

The server sends the manga folder name to MyAnimeList when a visible title needs metadata. Only unique exact matches against primary or alternate titles are applied automatically. For a missing or incorrect match, an administrator can open the title, select **Identify manga**, search an English or Japanese name, and choose the correct entry. Other readers cannot change the shared match.

Metadata is stored under Jellyfin's data directory in `manga-reader/metadata`, refreshed after seven days, and retained during provider outages. Unmatched searches are cached for one day. Requests are serialized and paced; rate-limit responses trigger a cooldown. Cover images are fetched by the server from MyAnimeList's HTTPS CDN and cached locally; image URLs never contain Jellyfin credentials. Library access is checked before serving metadata or covers. Manga reading remains available when the provider is offline.

These cards and details appear in the plugin's in-app Web/Android reader. This release does not replace Jellyfin's native Books metadata providers or add manga support to Roku/Android TV.

Identify manga also accepts a MyAnimeList manga URL or numeric manga ID. This bypasses title search, but still depends on MyAnimeList being able to return that individual entry. Provider outages are explained in the reader instead of showing a generic HTTP 503.

The saved Client ID is stored in the Jellyfin plugin configuration on the server and is sent only to api.myanimelist.net in the X-MAL-CLIENT-ID request header. It is not returned to the reader, echoed into settings, or sent with cover-image requests. Administrators can save, test, replace, or remove it without restarting Jellyfin. Back up and protect server configuration as you would other application settings.
Folder grouping and metadata are keyed by a stable hash of the physical containing directory, not Jellyfin's logical parent ID. This keeps separate series separate when Jellyfin assigns their books the same parent. Upgrading from earlier releases refreshes metadata under the new folder identities instead of reusing potentially shared matches. Existing chapter IDs and reading progress are unchanged; an ambiguous title may need identification again.

## Portable manga identification and folder names

After a successful match, the plugin saves `manga-reader.json` in that manga folder in a registered Manga library. It contains a format version, MAL ID, primary title and English title; no API credentials or user information. Jellyfin needs write access to the folder. The reader shows a warning if this file cannot be saved.

After reinstalling Jellyfin, enable the Manga library and open it. The plugin reads this file before attempting title matching, restores the saved identity even without API access, and fetches full details and cover art by ID when the MAL Client ID is configured. Back up the file with the manga. It preserves identification, not accounts or reading progress. Invalid files produce an actionable error rather than silently guessing another manga; identifying the manga again replaces the file.

Administrators can open a manga and select **Rename folder**, choose English title (with MAL ID fallback) or numeric MAL ID, preview the proposed name, then apply it. Identification alone never renames folders. The button moves the folder within its existing parent, carries its identification file, preserves existing per-user chapter progress, and requests a library scan. Close readers before applying the rename and refresh after the scan. Existing destination folders, library roots, linked directories, and conflicting destination progress are refused; folders are never merged. Renaming through another file manager does not migrate reading progress.