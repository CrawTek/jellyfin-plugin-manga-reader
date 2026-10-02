'use strict';
const $ = id => document.getElementById(id);
const root = new URL('../', location.href);
const key = 'manga-reader:' + root.pathname;
const embedded = new URLSearchParams(location.search).get('embedded') === '1' && parent !== window;
const libraryId = new URLSearchParams(location.search).get('libraryId') || '';
let auth;
try { auth = embedded ? null : JSON.parse(sessionStorage.getItem(key) || 'null'); } catch { auth = null; }
let library = [], cursor = 0, current = null, pageUrl = null, loading = false;
let saveChain = Promise.resolve(), unsaved = false, pendingSaves = 0;
let device = localStorage.getItem('manga-reader-device');
if (!device) { device = Array.from(crypto.getRandomValues(new Uint8Array(16)), v => v.toString(16).padStart(2,'0')).join(''); localStorage.setItem('manga-reader-device', device); }
const authHeader = () => embedded ? 'MediaBrowser Token=' + JSON.stringify(auth?.token || '') : `MediaBrowser Client="Manga Reader", Device="Browser", DeviceId="${device}", Version="0.2.3"${auth ? `, Token="${auth.token}"` : ''}`;
function message(text = '') { $('status').textContent = text; $('status').hidden = !text; }
async function request(path, options = {}) {
    const response = await fetch(new URL(path, root), { ...options, headers: { Authorization: authHeader(), ...options.headers } });
    if (!response.ok) {
        if (response.status === 401) throw new Error('Sign-in expired or incorrect. Sign out and sign in again.');
        if (response.status === 409) { const error = new Error('Your place changed in another reader. Return to the library and reopen this book.'); error.conflict = true; throw error; }
        throw new Error(`Request failed (${response.status}). Check your connection, library access, or the server log.`);
    }
    return response;
}
async function json(path, options) { return (await request(path, options)).json(); }
function view(name) { for (const id of ['login', 'library', 'reader']) $(id).hidden = id !== name; $('logout').hidden = embedded || !auth; message(); }
function card(book) {
    const button = document.createElement('button'); button.className = 'book';
    const icon = document.createElement('span'); icon.className = 'book-icon'; icon.textContent = '▤';
    const title = document.createElement('span'); title.className = 'book-title'; title.textContent = book.title;
    const info = document.createElement('span'); info.className = 'book-progress';
    info.textContent = book.progress ? `Resume · Page ${book.progress.Page ?? book.progress.page} of ${book.progress.Total ?? book.progress.total}` : 'Start reading →';
    button.append(icon, title, info); button.onclick = () => openBook(book.id).catch(e => message(e.message));
    return button;
}
function renderLibrary() {
    const query = $('search').value.toLowerCase();
    const books = library.filter(b => b.title.toLowerCase().includes(query));
    $('books').replaceChildren(...books.sort((a,b) => a.title.localeCompare(b.title, undefined, {numeric:true})).map(card));
    const recent = books.filter(b => b.progress).sort((a,b) => new Date(b.progress.UpdatedAt ?? b.progress.updatedAt) - new Date(a.progress.UpdatedAt ?? a.progress.updatedAt)).slice(0,6);
    $('continue').replaceChildren(...recent.map(card)); $('continueHeading').hidden = !recent.length;
    $('empty').hidden = books.length > 0; $('more').hidden = cursor === null;
}
async function loadLibrary(reset = false) {
    if (reset) { library = []; cursor = 0; }
    $('more').disabled = true;
    try {
        do {
            const data = await json(`MangaReader/library?start=${cursor}&libraryId=${encodeURIComponent(libraryId)}`);
            library.push(...data.items); cursor = data.next ?? null;
        } while (cursor !== null && library.length === 0);
        renderLibrary();
    } finally { $('more').disabled = false; }
}
async function openBook(id) {
    await saveChain;
    if (unsaved) throw new Error('Your last page has not been saved. Retry saving before opening another book.');
    const data = await json(`MangaReader/books/${id}`);
    const p = data.progress;
    current = { id, total: data.total, page: 0, revision: p?.Revision ?? p?.revision ?? 0 };
    $('direction').value = p?.Direction ?? p?.direction ?? 'rtl';
    $('title').textContent = data.title; $('total').textContent = `of ${data.total}`; $('pageNumber').max = data.total;
    view('reader'); await showPage(Math.min(p?.Page ?? p?.page ?? 1, data.total));
}
function save() {
    if (!current?.page || current.conflict) return;
    const book = current, page = current.page, direction = $('direction').value;
    unsaved = true; pendingSaves++; $('saved').textContent = 'Saving…';
    saveChain = saveChain.then(async () => {
        try {
            if (book.conflict) return;
            const result = await json(`MangaReader/books/${book.id}/progress`, {method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({page, direction, revision:book.revision})});
            book.revision = result.Revision ?? result.revision;
            const item = library.find(b => b.id === book.id); if (item) item.progress = result;
            if (current === book && current.page === page && $('direction').value === direction) { unsaved = false; $('saved').textContent = 'Place saved ✓'; }
        } catch (e) { book.conflict = !!e.conflict; unsaved = !e.conflict; $('saved').textContent = e.conflict ? 'Reopen to sync your place' : 'Not saved — click to retry'; message(e.message); }
        finally { pendingSaves--; }
    });
}
async function showPage(page) {
    if (!current || current.conflict || loading || page < 1 || page > current.total) return;
    loading = true; $('previous').disabled = $('next').disabled = true; message();
    let candidate;
    try {
        const response = await request(`MangaReader/books/${current.id}/pages/${page}`);
        candidate = URL.createObjectURL(await response.blob());
        const image = new Image(); image.src = candidate; await image.decode();
        $('pageImage').src = candidate; $('pageImage').alt = `${$('title').textContent}, page ${page}`;
        if (pageUrl) URL.revokeObjectURL(pageUrl); pageUrl = candidate; candidate = null;
        current.page = page; $('pageNumber').value = page; window.scrollTo(0, 0); save();
    } catch(e) { message(e.message); $('pageNumber').value = current.page || 1; }
    finally { if(candidate) URL.revokeObjectURL(candidate); loading = false; $('previous').disabled = current.page <= 1; $('next').disabled = current.page >= current.total; }
}
async function back() {
    await saveChain;
    if (unsaved) { message('Your place has not been saved. Click the save status to retry before leaving.'); return; }
    if (document.fullscreenElement) await document.exitFullscreen();
    current = null; view('library'); renderLibrary();
}
$('loginForm').onsubmit = async e => {
    e.preventDefault(); message(); const button = e.target.querySelector('button'); button.disabled = true;
    try {
        auth = null;
        const result = await json('Users/AuthenticateByName', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Username:$('username').value,Pw:$('password').value})});
        auth = {token:result.AccessToken}; sessionStorage.setItem(key, JSON.stringify(auth)); $('password').value = '';
        view('library'); await loadLibrary(true);
    } catch(e) { message(e.message); } finally { button.disabled = false; }
};
$('logout').onclick = async () => { await saveChain; if (unsaved) { message('Your place is not saved. Retry saving before signing out.'); return; } try { await request('Sessions/Logout', {method:'POST'}); } catch {} auth = null; current = null; library = []; sessionStorage.removeItem(key); view('login'); };
$('back').onclick = () => back().catch(e => message(e.message));
$('search').oninput = renderLibrary;
$('more').onclick = () => loadLibrary().catch(e => message(e.message));
$('previous').onclick = () => showPage(current.page - 1);
$('next').onclick = () => showPage(current.page + 1);
$('pageNumber').onchange = () => { const value = Number($('pageNumber').value); if (Number.isInteger(value) && value >= 1 && value <= current.total) showPage(value); else $('pageNumber').value = current.page; };
$('direction').onchange = save;
$('fit').onchange = () => $('stage').classList.toggle('width', $('fit').value === 'width');
$('saved').onclick = () => { if (unsaved) save(); };
$('fullscreen').onclick = async () => { try { if(document.fullscreenElement) await document.exitFullscreen(); else await $('reader').requestFullscreen(); } catch { message('Fullscreen is unavailable in this browser.'); } };
document.addEventListener('keydown', e => {
    if (!current || ['INPUT','SELECT','BUTTON'].includes(document.activeElement.tagName)) return;
    if (e.key === 'Escape') { back(); return; }
    if (['ArrowLeft','ArrowRight'].includes(e.key)) { e.preventDefault(); const forward = (e.key === 'ArrowLeft') === ($('direction').value === 'rtl'); showPage(current.page + (forward ? 1 : -1)); }
});
let touch;
$('stage').addEventListener('touchstart', e => { if(e.touches.length === 1) touch = [e.touches[0].clientX,e.touches[0].clientY]; else touch = null; }, {passive:true});
$('stage').addEventListener('touchend', e => { if (!touch || !current) return; const dx = e.changedTouches[0].clientX-touch[0], dy=e.changedTouches[0].clientY-touch[1]; touch=null; if (Math.abs(dx)>65 && Math.abs(dx)>Math.abs(dy)*1.5) showPage(current.page + ((dx>0) === ($('direction').value==='rtl') ? 1 : -1)); }, {passive:true});
window.addEventListener('beforeunload', e => { if(unsaved || pendingSaves) { e.preventDefault(); e.returnValue=''; } });
if (embedded) {
    document.querySelector('header').hidden = true;
    view('waiting'); message('Opening your Jellyfin library…');
    window.addEventListener('message', async e => {
        if (e.source !== parent || e.origin !== location.origin) return;
        if (e.data?.type === 'manga-session' && typeof e.data.token === 'string' && !auth) {
            auth = {token:e.data.token}; view('library');
            loadLibrary(true).catch(error => message(error.message));
        } else if (e.data?.type === 'manga-close-request') {
            await saveChain;
            if (loading || unsaved || pendingSaves) { message('Your page is still loading or has not been saved. Retry saving before leaving.'); return; }
            parent.postMessage({type:'manga-close-ok'}, location.origin);
        }
    });
    parent.postMessage({type:'manga-ready'}, location.origin);
} else if(auth) { view('library'); loadLibrary(true).catch(e => message(e.message)); } else view('login');




