# Manga Reader for Jellyfin

A browser-based reader for local CBZ/ZIP manga in Jellyfin Books libraries, with page turning and a separate saved place for every user.

**Preview release · Jellyfin 12.1.0 · .NET 10**

## Features

- Right-to-left manga controls, left-to-right option, arrow keys, and touch swipes.
- Jump to a page, fit page/width, and fullscreen.
- Exact page and direction saved on the server after the image loads.
- Continue-reading shelf, search, and paginated library loading.
- Progress follows the user's Jellyfin account across browsers/devices and survives server restarts.
- Concurrent readers detect conflicting progress instead of silently overwriting it.
- Jellyfin authentication and library permissions enforced on book, page, and progress requests.
- Self-contained reader assets: no CDN, separate service, or web-client file modification.

## Installation

Once a tagged GitHub release has been published:

1. Add this repository URL: `https://github.com/CrawTek/jellyfin-plugin-manga-reader/releases/latest/download/manifest.json`.
2. In Jellyfin, open **Dashboard → Plugins → Repositories** and add that URL.
3. Find **Manga Reader** in the catalog, install it, and restart Jellyfin.
4. Open **Dashboard → Plugins → Manga Reader → Open Manga Reader**.
5. Share the reader URL with your users. Each signs in with their own Jellyfin account.

For manual installation, extract the release ZIP to a `MangaReader` directory under Jellyfin's plugins directory, then restart the server. It contains `Jellyfin.Plugin.MangaReader.dll`.

The reader is served at `<Jellyfin base URL>/MangaReader/reader`. A configured Jellyfin base path is supported. Administrators may add this URL to Jellyfin Web's `config.json` custom `menuLinks` list, preserving existing entries. A menu entry is optional; the shared reader link works directly.

## Library and formats

Add the manga to a **Books** library and scan it using Jellyfin. This release reads `.cbz` and `.zip` files containing JPEG, PNG, WebP, GIF, or AVIF images. Pages use natural filename order: `1`, `2`, `10`. Images remain in the archive; nothing is extracted alongside the book. Hidden metadata files are ignored. Individual images are limited to 40 MB uncompressed; archives are limited to 20,000 entries.

PDF, CBR/RAR, EPUB, and loose image folders are not supported in this first release. Native TV/mobile clients do not gain a new reader from a server plugin; use the browser interface on desktop or mobile. ZIP discovery depends on the server recognizing the file as a Book; CBZ is preferred.

## Progress and privacy

Progress lives under Jellyfin's data directory in `manga-reader/<user-id>/<book-id>.json`. Include this directory in backups. It uses Jellyfin item IDs, so deleting and re-adding a library item may create a new reading record. Progress is plugin-specific and does not overwrite Jellyfin video playback data or the built-in book reader's progress.

The reader uses a Jellyfin login token kept in session storage for that browser tab. Passwords are sent only to the same Jellyfin server and are not stored. Use HTTPS for access over untrusted networks. A failed save is shown explicitly and leaving with unsaved progress prompts a browser warning. On a multi-device conflict, return to the library and reopen the book to use the server's latest place.

## Build, test, and publish

Install the .NET 10 SDK, then:

```powershell
dotnet build src -c Release
dotnet run --project tests/CoreTests.csproj -c Release
./scripts/package.ps1 -Repository 'CrawTek/jellyfin-plugin-manga-reader'
```

The packaging script creates a release ZIP and Jellyfin repository manifest in `artifacts/`, including the ZIP checksum and the actual GitHub repository URL. It never embeds the developer's server address or credentials.

For browser tests, run `npm install` then `npm test` with Microsoft Edge installed. Set `BROWSER_CHANNEL=chrome` to use Chrome. These tests use a mock server with original test artwork; they cover UI behavior, not live Jellyfin integration.

Push the source to GitHub and create a `v0.1.0` tag. The release workflow builds, runs the core tests, packages, and uploads both files as GitHub release assets. Release only after testing on a real Jellyfin server. Building successfully does not establish runtime compatibility or permission enforcement.

## API

All data endpoints require a user login. Server API keys without a user identity cannot read manga.

- `GET /MangaReader/library?start=0` — accessible CBZ/ZIP books and saved progress, in batches of 100 server items.
- `GET /MangaReader/books/{id}` — title, page count, and current user's progress.
- `GET /MangaReader/books/{id}/pages/{page}` — one-based image page.
- `PUT /MangaReader/books/{id}/progress` — `{ "page": 12, "direction": "rtl", "revision": 3 }`. A stale revision returns HTTP 409.

The reader HTML/CSS/JavaScript is public; book content and progress require authentication. Page responses disable shared caching.

## License

GNU GPL version 2. See [LICENSE](LICENSE).
