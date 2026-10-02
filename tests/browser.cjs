const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const http = require('node:http');

(async () => {
 const web = path.resolve(__dirname, '../src/Web');
 let progress = null, revision = 0, failSave = false, conflict = false, registered = false, failRegistration = false, emptyLibrary = false;
 const server = http.createServer(async (req,res) => {
  const url = new URL(req.url, 'http://localhost');
  const send = (data, status=200) => { res.writeHead(status, {'Content-Type':'application/json'});res.end(JSON.stringify(data)); };
  const relative = url.pathname.replace('/jellyfin/', '');
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
  if(relative==='MangaReader/bootstrap') return send({enabled:true,libraries:registered?[{id:'11111111111111111111111111111111',name:'My Manga'}]:[]});
  if(relative.startsWith('MangaReader/libraries/')) { if(failRegistration)return send({},500);registered=true;return send({}); }
  // Match Jellyfin's omission of null properties on the last page.
  if(relative==='MangaReader/library') return send({items:emptyLibrary?[]:[{id:'book',title:'The Paper Moon — Volume 1',progress}]});
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
  await page.locator('#username').fill('reader');await page.locator('#password').fill('test');await page.locator('button[type=submit]').click();
  await page.locator('#books .book').waitFor();assert.equal(await page.locator('#more').isVisible(),false);
  await page.locator('#books .book').click();await page.getByText('Place saved ✓',{exact:true}).waitFor();
  assert.equal(progress.Page,1);
  await page.locator('#stage').focus();await page.keyboard.press('ArrowLeft');await page.waitForFunction(()=>document.querySelector('#pageNumber').value==='2' && document.querySelector('#saved').textContent==='Place saved ✓');
  assert.equal(progress.Page,2);assert.equal(progress.Direction,'rtl');
  await page.reload();await page.locator('#continue .book').click();await page.getByText('Place saved ✓',{exact:true}).waitFor();assert.equal(await page.locator('#pageNumber').inputValue(),'2');
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
  await reader.locator('#books .book').click();await reader.getByText('Place saved ✓',{exact:true}).waitFor();
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
  await shell.locator('#mangaTile').click();await reader.locator('#continue .book').click();await reader.getByText('Place saved ✓',{exact:true}).waitFor();
  assert.equal(await reader.locator('#pageNumber').inputValue(),'3');
  await shell.evaluate(()=>window.testToken='');await shell.locator('#manga-reader-overlay').waitFor({state:'detached'});
  await shell.evaluate(()=>window.testToken='test-token');
  await shell.waitForTimeout(2200);
  failRegistration=true;
  const dialog=shell.waitForEvent('dialog');await shell.locator('button[type=submit]').click();
  const warning=await dialog;assert.match(warning.message(),/library was created.*Do not create it again/);await warning.accept();
  await shell.waitForFunction(()=>document.body.dataset.created==='3');
  emptyLibrary=true;await page.reload();await page.locator('#empty').waitFor();
  assert.equal(await page.locator('#more').isVisible(),false);assert.equal(await page.locator('#status').isVisible(),false);
  assert.deepEqual(errors,[]);
  console.log('PASS integration: ordinary Books unchanged, Manga preset/native folder arguments, library registration, same-window session handoff, resume, failed-save back guard, Android navigation hook, account logout, registration recovery warning');
 } finally { await browser.close();server.close(); }
})().catch(e=>{console.error(e);process.exit(1);});
