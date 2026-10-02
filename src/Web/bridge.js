(() => {
    'use strict';
    if (window.mangaReaderBridgeLoaded) return;
    window.mangaReaderBridgeLoaded = true;
    const script = document.currentScript;
    const root = new URL('../../', script.src);
    const sameServer = () => {
        const api = window.ApiClient;
        if (!api?.accessToken?.() || !api?.getCurrentUserId?.()) return null;
        const server = new URL(api.serverAddress(), location.href);
        return server.origin === root.origin && server.pathname.replace(/\/$/, '') === root.pathname.replace(/\/$/, '') ? api : null;
    };
    const normalize = value => String(value || '').replace(/-/g, '').toLowerCase();
    let identity = '', libraries = [], overlay, iframe, returning = false, pending = false;
    const wrapped = new WeakSet();
    const navigations = new WeakSet();
    const style = document.createElement('link'); style.rel = 'stylesheet'; style.href = new URL('MangaReader/assets/bridge.css', root); document.head.append(style);
    const post = data => iframe?.contentWindow?.postMessage(data, root.origin);
    function remove() { overlay?.remove(); overlay = iframe = null; document.body.classList.remove('manga-reader-open'); }
    function open(library) {
        if (overlay || !library || !sameServer()) return;
        overlay = document.createElement('section'); overlay.id = 'manga-reader-overlay'; overlay.setAttribute('role', 'dialog'); overlay.setAttribute('aria-label', 'Manga Reader');
        const bar = document.createElement('nav'); const close = document.createElement('button'); close.textContent = '← Jellyfin'; close.onclick = () => post({type:'manga-close-request'});
        const title = document.createElement('strong'); title.textContent = library.name; bar.append(close, title);
        iframe = document.createElement('iframe'); iframe.title = 'Manga library and reader'; iframe.allow = 'fullscreen'; iframe.src = new URL('MangaReader/reader?embedded=1&libraryId=' + encodeURIComponent(library.id), root);
        overlay.append(bar, iframe); document.body.append(overlay); document.body.classList.add('manga-reader-open');
        history.pushState({...history.state, mangaReaderOpen:true}, '', location.href);
    }
    window.addEventListener('message', e => {
        if (!iframe || e.source !== iframe.contentWindow || e.origin !== root.origin) return;
        if (e.data?.type === 'manga-ready') {
            const api = sameServer(); if (!api) { remove(); return; }
            // Same-origin, exact-frame handoff. Never put credentials in URLs or shared storage.
            post({type:'manga-session', token:api.accessToken(), userId:api.getCurrentUserId()});
        } else if (e.data?.type === 'manga-close-ok') {
            remove(); if (history.state?.mangaReaderOpen) { returning = true; history.back(); }
        }
    });
    window.addEventListener('popstate', () => {
        if (returning) { returning = false; return; }
        if (overlay) {
            // Keep the history boundary while the reader flushes its last page save.
            history.pushState({...history.state, mangaReaderOpen:true}, '', location.href);
            post({type:'manga-close-request'});
        }
    });
    document.addEventListener('keydown', e => { if (overlay && e.key === 'Escape') { e.preventDefault(); post({type:'manga-close-request'}); } }, true);
    function matchesLink(element, libraryId) {
        if (normalize(element.dataset?.id || element.dataset?.itemid) === libraryId) return true;
        if (!element.href) return false;
        try {
            const url = new URL(element.href, location.href);
            if (url.origin !== root.origin) return false;
            const query = url.hash.includes('?') ? url.hash.split('?')[1] : url.search;
            const params = new URLSearchParams(query);
            return ['topParentId', 'parentId', 'id', 'itemId'].some(key => normalize(params.get(key)) === libraryId);
        } catch { return false; }
    }
    document.addEventListener('click', e => {
        if (overlay || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        if (e.target.closest('button, .dlg-librarycreator') || /dashboard|configurationpage|librarysetup|libraries/i.test(location.hash)) return;
        const selected = libraries.find(library => e.composedPath().some(node => node instanceof Element && matchesLink(node, normalize(library.id))));
        if (selected) {
            e.preventDefault(); e.stopImmediatePropagation(); open(selected);
        }
    }, true);
    // Reuse Jellyfin's Books options, validation, directory picker and creation API.
    // The duplicate value is intentional: selecting the Manga label still submits a valid Books type.
    function enhanceLibraryCreator() {
        const api = sameServer();
        if (!api || typeof api.addVirtualFolder !== 'function') return;
        if (!wrapped.has(api)) {
            const create = api.addVirtualFolder;
            api.addVirtualFolder = async function(name, type, ...args) {
                const manga = type === 'books' && document.querySelector('.dlg-librarycreator #selectCollectionType')?.selectedOptions[0]?.dataset.mangaPreset === 'true';
                const result = await create.call(this, name, type, ...args);
                if (manga) {
                    try {
                        const folders = await api.getVirtualFolders();
                        const candidates = folders.filter(folder => folder.Name === name && String(folder.CollectionType).toLowerCase() === 'books');
                        if (candidates.length !== 1) throw new Error('Could not identify the new library.');
                        const response = await fetch(new URL('MangaReader/libraries/' + encodeURIComponent(candidates[0].ItemId), root), {method:'POST',headers:{Authorization:'MediaBrowser Token=' + JSON.stringify(api.accessToken())}});
                        if (!response.ok) throw new Error('Could not enable the manga reader.');
                        identity = ''; await refresh();
                    } catch {
                        window.alert('Your library was created, but Manga Reader setup did not finish. Open Dashboard → Plugins → Manga Reader and enable this existing library. Do not create it again.');
                    }
                }
                return result;
            };
            wrapped.add(api);
        }
        for (const select of document.querySelectorAll('.dlg-librarycreator #selectCollectionType')) {
            if (!select.querySelector('option[value="books"]') || select.querySelector('[data-manga-preset]')) continue;
            const option = document.createElement('option'); option.value = 'books'; option.textContent = 'Manga'; option.dataset.mangaPreset = 'true';
            select.append(option);
        }
    }
    new MutationObserver(enhanceLibraryCreator).observe(document.documentElement, {childList:true,subtree:true});
    async function refresh() {
        enhanceLibraryCreator();
        const navigation = window.NavigationHelper;
        if (navigation?.goBack && !navigations.has(navigation)) {
            const back = navigation.goBack;
            navigation.goBack = function(...args) { if (overlay) post({type:'manga-close-request'}); else return back.apply(this, args); };
            navigations.add(navigation);
        }
        const api = sameServer(); const next = api ? api.getCurrentUserId() + ':' + api.accessToken() : '';
        if (next === identity || pending) return;
        identity = next; libraries = []; remove();
        if (!api) return;
        pending = true;
        try {
            const response = await fetch(new URL('MangaReader/bootstrap', root), {headers:{Authorization:'MediaBrowser Token=' + JSON.stringify(api.accessToken())}});
            if (!response.ok) throw new Error('Manga integration unavailable');
            const data = await response.json();
            if (identity === next && data.enabled) libraries = data.libraries || [];
        } catch { identity = ''; }
        finally { pending = false; }
    }
    refresh(); setInterval(refresh, 2000);
})();

