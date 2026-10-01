export default function(view) {
    const status = view.querySelector('#mangaConfigStatus');
    const select = view.querySelector('#mangaExistingLibrary');
    view.addEventListener('viewshow', async () => {
        try {
            const folders = await ApiClient.getVirtualFolders();
            select.replaceChildren(...folders.filter(f => String(f.CollectionType).toLowerCase() === 'books').map(f => {
                const option = document.createElement('option'); option.value = f.ItemId; option.textContent = f.Name; return option;
            }));
            status.textContent = select.options.length ? '' : 'No Books libraries found. Create a Manga library from Dashboard → Libraries.';
        } catch { status.textContent = 'Could not load libraries. Try reopening this page.'; }
    });
    view.querySelector('form').addEventListener('submit', async e => {
        e.preventDefault(); if (!select.value) return;
        const button = e.target.querySelector('button'); button.disabled = true;
        try {
            const result = await fetch(ApiClient.getUrl('MangaReader/libraries/' + encodeURIComponent(select.value)), {method:'POST',headers:{'X-Emby-Token':ApiClient.accessToken()}});
            if (!result.ok) throw new Error();
            status.textContent = 'Enabled. Reload Jellyfin or reopen the Android app, then select this library on the home screen.';
        } catch { status.textContent = 'Could not enable this library. Check your administrator session and try again.'; }
        finally { button.disabled = false; }
    });
}
