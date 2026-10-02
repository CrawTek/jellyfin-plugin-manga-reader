export default function(view) {
    const status = view.querySelector('#mangaConfigStatus');
    const select = view.querySelector('#mangaExistingLibrary');
    const key = view.querySelector('#malClientId'), malStatus = view.querySelector('#malSettingsStatus');
    const configured = view.querySelector('#malConfiguredStatus');
    const buttons = ['#malSave','#malTest','#malClear'].map(id => view.querySelector(id));
    async function settingsRequest(path = '', method = 'GET', body) {
        const result = await fetch(ApiClient.getUrl('MangaReader/settings/mal' + path), {method, headers:{Authorization:'MediaBrowser Token=' + JSON.stringify(ApiClient.accessToken()),'Content-Type':'application/json'}, ...(body === undefined ? {} : {body:JSON.stringify(body)})});
        if (!result.ok) { let error; try { error = await result.json(); } catch {} throw new Error(error?.detail || error?.Detail || 'Could not update MyAnimeList settings. Check your administrator session.'); }
        return result.json();
    }
    function showConfigured(value) {
        configured.textContent = value ? 'A Client ID is saved. Leave the field empty to keep it; enter a new one to replace it.' : 'No Client ID saved. Add one to enable cover art, ratings, and descriptions.';
        view.querySelector('#malClear').hidden = !value;
    }
    async function updateMal(action) {
        buttons.forEach(button => button.disabled = true); malStatus.textContent = '';
        try {
            if (action === 'test') {
                await settingsRequest('/test','POST',{clientId:key.value.trim()});
                malStatus.textContent = key.value.trim() ? 'Connection successful. Save this Client ID to use it.' : 'Connection successful with the saved Client ID.';
            } else {
                const value = action === 'clear' ? '' : key.value.trim();
                if (action === 'save' && !value) { malStatus.textContent = 'Enter a Client ID to save. The existing value has not changed.'; return; }
                const result = await settingsRequest('','PUT',{clientId:value}); showConfigured(result.configured); key.value = '';
                malStatus.textContent = action === 'clear' ? 'Client ID removed. Cached metadata and reading progress are preserved.' : 'Saved. Reopen the manga reader to load metadata. No server restart is needed.';
            }
        } catch(error) { malStatus.textContent = error.message; }
        finally { buttons.forEach(button => button.disabled = false); }
    }
    view.querySelector('#malSettingsForm').addEventListener('submit', e => { e.preventDefault(); updateMal('save'); });
    view.querySelector('#malTest').addEventListener('click', () => updateMal('test'));
    view.querySelector('#malClear').addEventListener('click', () => updateMal('clear'));
    view.addEventListener('viewshow', async () => {
        key.value = ''; malStatus.textContent = '';
        settingsRequest().then(result => showConfigured(result.configured)).catch(error => { malStatus.textContent = error.message; });
        try {
            const folders = await ApiClient.getVirtualFolders();
            select.replaceChildren(...folders.filter(f => String(f.CollectionType).toLowerCase() === 'books').map(f => {
                const option = document.createElement('option'); option.value = f.ItemId; option.textContent = f.Name; return option;
            }));
            status.textContent = select.options.length ? '' : 'No Books libraries found. Create a Manga library from Dashboard → Libraries.';
        } catch { status.textContent = 'Could not load libraries. Try reopening this page.'; }
    });
    view.querySelector('#mangaLibraryForm').addEventListener('submit', async e => {
        e.preventDefault(); if (!select.value) return;
        const button = e.target.querySelector('button'); button.disabled = true;
        try {
            const result = await fetch(ApiClient.getUrl('MangaReader/libraries/' + encodeURIComponent(select.value)), {method:'POST',headers:{Authorization:'MediaBrowser Token=' + JSON.stringify(ApiClient.accessToken())}});
            if (!result.ok) throw new Error(`Server returned ${result.status}.`);
            status.textContent = 'Enabled. Reload Jellyfin or reopen the Android app, then select this library on the home screen.';
        } catch (error) { status.textContent = 'Could not enable this library. ' + error.message + ' Check your administrator session and try again.'; }
        finally { button.disabled = false; }
    });
}

