const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const http = require('node:http');

(async () => {
 const web = path.resolve(__dirname, '../src/Web');
 let savedClientId = '';
 let renamed = false;
 let progress = null, revision = 0, failSave = false, conflict = false, registered = false, failRegistration = false, emptyLibrary = false, groupedFixture = false, failMetadataSearch = false, metadataFixture = false, identified = false, admin = true;
 const mangaInfo = () => ({id:42,title:'Tsuki no Kami',englishTitle:'The Paper Moon',score:8.25,synopsis:'An original test story about a paper moon.',genres:['Adventure'],status:'Publishing',image:'https://cdn.myanimelist.net/images/manga/test.jpg'});
 const server = http.createServer(async (req,res) => {
  const url = new URL(req.url, 'http://localhost');
  const send = (data, status=200) => { res.writeHead(status, {'Content-Type':'application/json'});res.end(JSON.stringify(data)); };
  const relative = url.pathname.replace('/jellyfin/', '');
  if(relative==='web/config-test.html') {
   res.writeHead(200,{'Content-Type':'text/html'});
   return res.end(fs.readFileSync(path.join(web,'config.html'),'utf8') + `<script>window.ApiClient={getUrl:p=>'/jellyfin/'+p,accessToken:()=> 'test-token',getVirtualFolders:async()=>[{ItemId:'library',Name:'Manga',CollectionType:'books'}]};</script><script type="module">import setup from '/jellyfin/MangaReader/assets/config.js';const view=document.querySelector('#mangaReaderConfig');setup(view);view.dispatchEvent(new Event('viewshow'));</script>`);
  }
  if(relative==='web/index.html') { res.writeHead(200,{'Content-Type':'text/html'});return res.end(fs.readFileSync(path.join(__dirname,'shell.html'))); }
  if(url.pathname.endsWith('/favicon.ico')) { res.writeHead(204); return res.end(); }
  if(relative==='MangaReader/reader' || relative.startsWith('MangaReader/assets/')) {
   const name = relative==='MangaReader/reader' ? 'index.html' : relative.split('/').pop();
   if(!['index.html','reader.js','reader.css','bridge.js','bridge.css','config.js'].includes(name)) return send({},404);
   res.writeHead(200,{'Content-Type':name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html'}); return res.end(fs.readFileSync(path.join(web,name)));
  }
  if(relative==='Users/AuthenticateByName') return send({AccessToken:'test-token'});
  // Jellyfin 12 may disable legacy X-Emby-Token authentication.
  assert.match(req.headers.authorization || '', /Token="test-token"/);
  if(relative==='MangaReader/settings/mal' || relative==='MangaReader/settings/mal/test') {
   if(req.method==='GET') return send({configured:!!savedClientId});
   let body='';for await(const chunk of req)body+=chunk;
   const value=JSON.parse(body).clientId;
   if(relative.endsWith('/test')) return (value || savedClientId)==='valid-client' ? send({connected:true}) : send({detail:'MyAnimeList rejected the Client ID.'},503);
   savedClientId=value;return send({configured:!!savedClientId});
  }
  if(relative==='MangaReader/bootstrap') return send({enabled:true,libraries:registered?[{id:'11111111111111111111111111111111',name:'My Manga'}]:[]});
  if(relative.startsWith('MangaReader/libraries/')) { if(failRegistration)return send({},500);registered=true;return send({}); }
  // Match Jellyfin's omission of null properties on the last page.
  if(relative==='MangaReader/library' && groupedFixture) return send(url.searchParams.get('start') === '0' ? {next:100,items:[{id:'ten',title:'Chapter 10',series:'Folder title',seriesId:'one'},{id:'other',title:'Chapter 1',series:'Other manga',seriesId:'two'}]} : {items:[{id:'two',title:'Chapter 2',series:'Folder title',seriesId:'one'},{id:'duplicate',title:'Chapter 1',series:'Folder title',seriesId:'separate-folder'}]});
  if(relative==='MangaReader/library') return send({canIdentify:admin,items:emptyLibrary?[]:[{id:'book',title:'The Paper Moon — Volume 1',series:'The Paper Moon',seriesId:'folder-one',progress}]});
  if(relative.endsWith('/metadata')) return send({details:metadataFixture ? mangaInfo() : null,notice:metadataFixture?'Identification saved beside your manga.':null});
  if(relative.endsWith('/rename-preview')) return send({sourceName:'Tsuki no Kami',targetName:'The Paper Moon',malId:42});
  if(relative.endsWith('/rename')) {let body='';for await(const chunk of req)body+=chunk;const value=JSON.parse(body);assert.equal(value.expectedName,'The Paper Moon');assert.equal(value.malId,42);renamed=true;return send({renamed:true,targetName:'The Paper Moon'});}
  if(relative.endsWith('/matches')) return failMetadataSearch ? send({detail:'MyAnimeList is temporarily unavailable. Try again later.'},503) : send([mangaInfo()]);
  if(relative.endsWith('/metadata/42')) {assert.equal(req.method,'PUT');identified=true;return send({details:mangaInfo()});}
  if(relative.endsWith('/cover')) {res.writeHead(200,{'Content-Type':'image/svg+xml'});return res.end('<svg xmlns="http://www.w3.org/2000/svg" width="400" height="600"><rect width="400" height="600" fill="#365444"/><circle cx="200" cy="220" r="110" fill="#d0edaa"/><text x="55" y="410" fill="white" font-size="30">THE PAPER MOON</text></svg>');}
  if(relative==='MangaReader/books/book') return send({id:'book',title:'The Paper Moon — Volume 1',total:3,progress});
  if(relative.includes('/pages/')) {
   const number = Number(relative.split('/').pop());
   res.writeHead(200,{'Content-Type':'image/svg+xml'});
   return res.end(`<svg xmlns="http://www.w3.org/2000/svg" width="700" height="1000"><rect width="700" height="1000" fill="#f7f3e8"/><rect x="35" y="35" width="630" height="400" fill="#dde3d3" stroke="#1c2820" stroke-width="5"/><circle cx="350" cy="200" r="100" fill="#283b2d"/><path d="M35 435L200 200L450 435" fill="#91a57c"/><rect x="35" y="460" width="300" height="470" fill="#d7d1c2" stroke="#222" stroke-width="4"/><rect x="355" y="460" width="310" height="470" fill="#c7d4c0" stroke="#222" stroke-width="4"/><text x="70" y="530" font-size="22">Reader test page ${number}</text><text x="390" y="840" font-size="32">${number} / 3</text></svg>`);
  }
  if(relative.endsWith('/progress')) {
   if(failSave) return send({},500);
   if(conflict) return send({},409);
   let body=''; for await (const chunk of req) body+=chunk;
   const value=JSON.parse(body); assert.equal(value.revision,revision);
   progress={Page:value.page,Total:3,Direction:value.direction,Revision:++revision,UpdatedAt:new Date().toISOString()}; return send(progress);
  }
  return send({},404);
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser = await chromium.launch({channel:process.env.BROWSER_CHANNEL || 'msedge',headless:true});
 try {
  const page=await browser.newPage({viewport:{width:1280,height:900}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/jellyfin/MangaReader/reader`);
  await page.locator('#username').fill('reader');await page.locator('#password').fill('test');await page.locator('#loginForm button[type=submit]').click();
  await page.locator('#books .book').waitFor();assert.equal(await page.locator('#more').isVisible(),false);
  await page.locator('#books .book').click();assert.equal(await page.locator('#libraryTitle').textContent(),'The Paper Moon');await page.locator('#books .book').click();await page.getByText('Place saved ✓',{exact:true}).waitFor();
  assert.equal(progress.Page,1);
  await page.locator('#stage').focus();await page.keyboard.press('ArrowLeft');await page.waitForFunction(()=>document.querySelector('#pageNumber').value==='2' && document.querySelector('#saved').textContent==='Place saved ✓');
  assert.equal(progress.Page,2);assert.equal(progress.Direction,'rtl');
  await page.reload();await page.locator('#books .book').click();await page.locator('#continue .book').click();await page.getByText('Place saved ✓',{exact:true}).waitFor();assert.equal(await page.locator('#pageNumber').inputValue(),'2');
  await page.locator('#direction').selectOption('ltr');await page.getByText('Place saved ✓',{exact:true}).waitFor();
  await page.locator('#stage').focus();await page.keyboard.press('ArrowRight');await page.waitForFunction(()=>document.querySelector('#pageNumber').value==='3' && document.querySelector('#saved').textContent==='Place saved ✓');
  assert.equal(progress.Page,3);assert.equal(await page.locator('#next').isDisabled(),true);
  failSave=true;await page.locator('#previous').click();await page.getByText('Not saved — click to retry',{exact:true}).waitFor();assert.equal(progress.Page,3);
  failSave=false;await page.locator('#saved').click();await page.getByText('Place saved ✓',{exact:true}).waitFor();assert.equal(progress.Page,2);
  conflict=true;await page.locator('#previous').click();await page.getByText('Reopen to sync your place',{exact:true}).waitFor();await page.locator('#back').click();assert.equal(await page.locator('#library').isVisible(),true);
  conflict=false;await page.locator('#continue .book').click();await page.getByText('Place saved ✓',{exact:true}).waitFor();assert.equal(await page.locator('#pageNumber').inputValue(),'2');
  fs.mkdirSync(path.resolve(__dirname,'../test-results'),{recursive:true});
  await page.screenshot({path:path.resolve(__dirname,'../test-results/reader-desktop.png'),fullPage:true});
  await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.resolve(__dirname,'../test-results/reader-mobile.png'),fullPage:true});
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  assert.deepEqual(errors,[]);
  console.log('PASS browser: login, base path, RTL/LTR, turn pages, reload/resume, failed save/retry, conflict/reopen, mobile width, no script errors');
  const shell=await browser.newPage({viewport:{width:390,height:844}});
  shell.on('pageerror',e=>errors.push(e.message));
  await shell.goto(`http://127.0.0.1:${server.address().port}/jellyfin/web/index.html`);
  await shell.locator('#selectCollectionType').selectOption({label:'Books'});
  await shell.locator('.btnAddFolder').click();await shell.locator('button[type=submit]').click();
  await shell.waitForFunction(()=>document.body.dataset.created==='1');assert.equal(registered,false);
  await shell.locator('#selectCollectionType').selectOption({label:'Manga'});
  assert.equal(await shell.locator('#txtValue').inputValue(),'Manga');
  await shell.locator('#txtValue').fill('My Manga');
  await shell.locator('button[type=submit]').click();
  await shell.waitForFunction(()=>document.body.dataset.created==='2');assert.equal(registered,true);
  const created=await shell.evaluate(()=>window.testCalls[1]);
  assert.equal(created.type,'books');assert.equal(created.name,'My Manga');assert.deepEqual(created.options.PathInfos,[{Path:'/media/manga'}]);
  await shell.locator('#mangaTile').click();
  const reader=shell.frameLocator('#manga-reader-overlay iframe');
  await reader.locator('#books .book').click();await reader.locator('#books .book').click();await reader.getByText('Place saved ✓',{exact:true}).waitFor();
  assert.equal(await reader.locator('#pageNumber').inputValue(),'2');
  assert.equal(await reader.locator('#logout').isVisible(),false);
  assert.equal(await reader.locator('#login').isVisible(),false);
  assert.equal(browser.contexts().flatMap(c=>c.pages()).length,2);
  failSave=true;await reader.locator('#next').click();await reader.getByText('Not saved — click to retry',{exact:true}).waitFor();
  await shell.evaluate(()=>NavigationHelper.goBack());
  await reader.getByText('Your page is still loading or has not been saved. Retry saving before leaving.',{exact:true}).waitFor();
  assert.equal(await shell.locator('#manga-reader-overlay').count(),1);
  failSave=false;await reader.locator('#saved').click();await reader.getByText('Place saved ✓',{exact:true}).waitFor();
  await shell.screenshot({path:path.resolve(__dirname,'../test-results/in-app-reader-mobile.png'),fullPage:true});
  await shell.evaluate(()=>NavigationHelper.goBack());await shell.locator('#manga-reader-overlay').waitFor({state:'detached'});
  assert.equal(await shell.evaluate(()=>window.nativeBackCount),0);
  await shell.locator('#mangaTile').click();await reader.locator('#books .book').click();await reader.locator('#continue .book').click();await reader.getByText('Place saved ✓',{exact:true}).waitFor();
  assert.equal(await reader.locator('#pageNumber').inputValue(),'3');
  await shell.evaluate(()=>window.testToken='');await shell.locator('#manga-reader-overlay').waitFor({state:'detached'});
  await shell.evaluate(()=>window.testToken='test-token');
  await shell.waitForTimeout(2200);
  failRegistration=true;
  const dialog=shell.waitForEvent('dialog').then(async warning=>{assert.match(warning.message(),/library was created.*Do not create it again/);await warning.accept();});
  await Promise.all([shell.locator('button[type=submit]').click(),dialog]);
  await shell.waitForFunction(()=>document.body.dataset.created==='3');
  groupedFixture=true;await page.reload();await page.getByRole('button',{name:'Folder title 2 chapters →',exact:true}).waitFor();
  assert.equal(await page.locator('#books .book').count(),3);
  assert.equal(await page.locator('#continue .book').count(),0);
  await page.getByRole('button',{name:'Folder title 2 chapters →',exact:true}).click();
  assert.deepEqual(await page.locator('#books .book-title').allTextContents(),['Chapter 2','Chapter 10']);
  await page.locator('#seriesBack').click();assert.equal(await page.locator('#books .book').count(),3);
  await page.locator('#search').fill('Other manga');assert.equal(await page.locator('#books .book').count(),1);
  await page.locator('#search').fill('');
  await page.screenshot({path:path.resolve(__dirname,'../test-results/folder-library.png'),fullPage:true});
  groupedFixture=false;metadataFixture=true;await page.reload();
  await page.locator('#books .manga-cover').waitFor();await page.getByText('★ 8.25 / 10 · MAL',{exact:true}).waitFor();
  await page.screenshot({path:path.resolve(__dirname,'../test-results/metadata-tiles-mobile.png'),fullPage:true});
  await page.locator('#books .book').click();await page.getByText('An original test story about a paper moon.',{exact:true}).waitFor();
  assert.equal(await page.locator('#libraryTitle').textContent(),'The Paper Moon');
  await page.getByText('Identification saved beside your manga.',{exact:true}).waitFor();
  await page.locator('#renameFolder').click();assert.equal(renamed,false);
  await page.locator('#renamePreview').click();await page.getByText('Tsuki no Kami → The Paper Moon',{exact:true}).waitFor();assert.equal(renamed,false);
  await page.locator('#renameApply').click();await page.getByText(/Folder renamed to The Paper Moon/).waitFor();assert.equal(renamed,true);
  await page.reload();await page.locator('#books .manga-cover').waitFor();await page.locator('#books .book').click();
  assert.equal(await page.locator('#malLink').getAttribute('href'),'https://myanimelist.net/manga/42');
  await page.locator('#identify').click();await page.locator('#identifySearch').click();await page.locator('#identifyResults button').click();
  await page.locator('#identifyPanel').waitFor({state:'hidden'});assert.equal(identified,true);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  await page.screenshot({path:path.resolve(__dirname,'../test-results/metadata-details-mobile.png'),fullPage:true});
  failMetadataSearch=true;await page.locator('#identify').click();await page.locator('#identifySearch').click();await page.getByText('MyAnimeList is temporarily unavailable. Try again later.',{exact:true}).waitFor();failMetadataSearch=false;await page.locator('#identifyQuery').fill('https://myanimelist.net/manga/42');await page.locator('#identifySearch').click();await page.locator('#identifyResults button').waitFor();admin=false;await page.reload();await page.locator('#books .book').click();assert.equal(await page.locator('#identify').isVisible(),false);
  emptyLibrary=true;await page.reload();await page.locator('#empty').waitFor();
  assert.equal(await page.locator('#more').isVisible(),false);assert.equal(await page.locator('#status').isVisible(),false);
  assert.deepEqual(errors,[]);
  const config=await browser.newPage();config.on('pageerror',e=>errors.push(e.message));
  await config.goto(`http://127.0.0.1:${server.address().port}/jellyfin/web/config-test.html`);
  await config.getByText('No Client ID saved.',{exact:false}).waitFor();
  await config.locator('#malClientId').fill('invalid-client');await config.locator('#malTest').click();await config.getByText('MyAnimeList rejected the Client ID.',{exact:true}).waitFor();
  assert.equal(savedClientId,'');
  await config.locator('#malClientId').fill('valid-client');await config.locator('#malTest').click();await config.getByText('Connection successful. Save this Client ID to use it.',{exact:true}).waitFor();assert.equal(savedClientId,'');
  await config.locator('#malSave').click();await config.getByText('Saved. Reopen the manga reader to load metadata. No server restart is needed.',{exact:true}).waitFor();assert.equal(savedClientId,'valid-client');
  assert.equal(await config.locator('#malClientId').inputValue(),'');
  await config.reload();await config.getByText('A Client ID is saved.',{exact:false}).waitFor();assert.equal(await config.locator('#malClientId').inputValue(),'');
  await config.locator('#malSave').click();await config.getByText('Enter a Client ID to save. The existing value has not changed.',{exact:true}).waitFor();assert.equal(savedClientId,'valid-client');
  await config.locator('#malTest').click();await config.getByText('Connection successful with the saved Client ID.',{exact:true}).waitFor();
  await config.locator('#malClear').click();await config.getByText('Client ID removed. Cached metadata and reading progress are preserved.',{exact:true}).waitFor();assert.equal(savedClientId,'');
  failRegistration=false;await config.locator('#mangaLibraryForm button').click();await config.getByText('Enabled. Reload Jellyfin or reopen the Android app, then select this library on the home screen.',{exact:true}).waitFor();
  assert.deepEqual(errors,[]);
  console.log('PASS integration: library registration, in-app reading, resume, metadata tiles, identification, official API settings save/test/remove, no key echo, existing library setup preserved');
 } finally { await browser.close();server.close(); }
})().catch(e=>{console.error(e);process.exit(1);});
