// Installed disposable fixture only. No .env, external delivery or production state.
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const { openBrowserRelay } = require('./browser-relay.cjs');
const { measureLivePoster } = require('./live-poster-measurement.cjs');
(async () => {
    assert.equal(process.env.DISPOSABLE_THEME_TEST, 'true');
    const upstream = new URL(process.env.THEME_TEST_BASE_URL);
    assert(['127.0.0.1', 'localhost'].includes(upstream.hostname));
    const relay = await openBrowserRelay(upstream);
    const base = relay.baseUrl;
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage(), errors = [], checks = [], failedRequests=[];let phase='login';
        page.on('pageerror', error => errors.push({phase,name:error.name,message:error.message,stack:error.stack?.split('\n').slice(0,5).join('\n')}));
        page.on('response',response=>{if(response.status()>=400)failedRequests.push({phase,path:new URL(response.url()).pathname,status:response.status()});});
        await page.goto(base + '/web/index.html#/configurationpage?name=Notifications');
        await page.locator('#txtManualName').fill('suite-admin');
        await page.locator('#txtManualPassword').fill('Disposable-Only-Suite-9714!');
        await page.locator('#txtManualPassword').press('Enter');
        await page.getByRole('heading', { name: 'Library events', exact: true }).waitFor();
        const request = (path, method='GET', body) => page.evaluate(async ({path,method,body}) => {
            const response = await ApiClient.ajax({ url: ApiClient.getUrl('Notifications/api/v1/' + path), type: method, contentType: 'application/json', dataType: 'json', data: body === undefined ? undefined : JSON.stringify(body) });
            if (!response.ok) throw Error('Installed notification API rejected request'); return response.data;
        }, {path,method,body});
        const initial = await request('notifications?limit=100');
        // Use the unique catalog summary instead of matching nested studio details.
        const summary = page.locator('summary').filter({ hasText: /Registered notification types/ }).first();
        if (!await summary.count()) throw Error('Notification registry summary missing');
        await summary.click();
        const registeredCatalogs = (await request('settings/notifications/types')).catalogs;
        const navigation = await page.locator('.notificationPluginCatalog').evaluateAll(nodes=>nodes.map(node=>({source:node.dataset.notificationSource,open:node.open,types:[...node.querySelectorAll('.notificationRegisteredType')].map(type=>type.dataset.notificationType).sort()})));
        assert.equal(navigation.length,registeredCatalogs.length);
        for(const catalog of registeredCatalogs){
            const groups=navigation.filter(group=>group.source===catalog.source);
            assert.equal(groups.length,1,'Each registering plugin needs its own navigation group');
            assert.equal(groups[0].open,false,'Plugin navigation starts collapsed');
            assert.deepEqual(groups[0].types,catalog.types.map(type=>type.type).sort());
        }
        for(const width of [320,1280]){
            await page.setViewportSize({width,height:1000});
            assert(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth+2),'Plugin catalog overflow');
            if(process.env.ADMIN_SCREENSHOTS)await page.locator('.notificationTypeCatalog').screenshot({path:process.env.ADMIN_SCREENSHOTS+'/notification-catalog-collapsed-'+width+'.png'});
        }
        const plugin = page.locator('.notificationPluginCatalog[data-notification-source="notifications"]');
        await plugin.locator(':scope > summary').click();
        for(const width of [320,1280]){
            await page.setViewportSize({width,height:1000});
            assert(await plugin.evaluate(node=>node.scrollWidth<=node.clientWidth+2),'Plugin type list overflow');
            if(process.env.ADMIN_SCREENSHOTS)await plugin.screenshot({path:process.env.ADMIN_SCREENSHOTS+'/notification-catalog-expanded-'+width+'.png'});
        }
        const registeredType = plugin.locator('.notificationRegisteredType[data-notification-type="library.movie.added"]');
        await registeredType.locator(':scope > summary').click();
        const type = page.getByRole('group',{name:'New movies',exact:true});
        await type.getByText('Example notification — not sent', { exact: true }).waitFor();
        assert(await type.locator('.notificationTypePreview').textContent());
        await type.getByText('Notification Studio', { exact: true }).click();
        await type.getByRole('button', { name: 'Add text', exact: true }).waitFor();
        assert.equal(await type.getByRole('button',{name:'Undo',exact:true}).count(),0);
        assert.equal(await type.getByRole('button',{name:'Redo',exact:true}).count(),0);
        // Opening the designer and saving untouched defaults must persist the
        // same left-poster layout that the example shows, not a null fallback.
        phase='default-layout';const defaultSaved=page.waitForResponse(r=>r.url().endsWith('/types/layout')&&r.request().method()==='PUT');
        await type.getByRole('button',{name:'Save layout',exact:true}).click();assert.equal((await defaultSaved).status(),200);
        const defaultCatalog=(await request('settings/notifications/types')).catalogs.find(c=>c.source==='notifications').types.find(t=>t.type==='library.movie.added');
        const defaultPoster=defaultCatalog.layout.canvas.layers.find(layer=>layer.kind==='image');
        assert.equal(defaultPoster.x,0);assert.equal(defaultPoster.fit,'contain');
        assert(defaultCatalog.layout.canvas.layers.filter(layer=>layer.kind!=='image').every(layer=>layer.x>defaultPoster.x+defaultPoster.width),'Default text must have a clear gap after the poster');
        for (const theme of ['dark','light']) {
            await page.evaluate(async theme => {
                document.documentElement.dataset.theme=theme;
                for (const link of document.querySelectorAll('link[href*="/themes/"]')) link.remove();
                const link=document.createElement('link');link.rel='stylesheet';link.href=new URL('themes/'+theme+'/theme.css',document.baseURI).href;
                await new Promise((resolve,reject)=>{link.onload=resolve;link.onerror=reject;document.head.append(link);});
            },theme);
            for (const width of [320,390,1280]) {
                phase='studio/'+theme+'/'+width;
                await page.setViewportSize({width,height:1000});
                const title=type.locator('.notificationStudioBoard [data-layer=title]');await title.scrollIntoViewIfNeeded();await title.focus();
                await title.press('ArrowLeft');await type.getByLabel('Font family',{exact:true}).selectOption('serif');
                await type.getByLabel('Font size',{exact:true}).fill('20');await type.getByLabel('Font size',{exact:true}).press('Tab');
                assert.equal(await page.evaluate(()=>document.activeElement?.dataset.studioField),'Font family','Native Tab order must survive a property edit');
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>node.style.fontFamily),'serif');
                const alignment=type.locator('.notificationStudioAlignment');if(!await alignment.evaluate(node=>node.open))await alignment.locator('summary').click();
                await type.getByLabel('Align relative to',{exact:true}).selectOption('');
                await type.getByRole('button',{name:'Center horizontally',exact:true}).click();
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>parseFloat(node.style.left)+parseFloat(node.style.width)/2),50);
                await type.getByRole('button',{name:'Center vertically',exact:true}).click();
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>parseFloat(node.style.top)+parseFloat(node.style.height)/2),50);
                await type.getByLabel('Align relative to',{exact:true}).selectOption('body');
                await type.getByRole('button',{name:'Center horizontally',exact:true}).click();await type.getByRole('button',{name:'Center vertically',exact:true}).click();
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>parseFloat(node.style.left)+parseFloat(node.style.width)/2),64);
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>parseFloat(node.style.top)+parseFloat(node.style.height)/2),67);
                for(let step=0;step<4;step++)await type.locator('.notificationStudioBoard [data-layer=title]').press('Control+z');
                await type.getByLabel('Align relative to',{exact:true}).selectOption('');await alignment.locator('summary').click();
                const image=type.locator('.notificationStudioBoard [data-layer=image]');await image.scrollIntoViewIfNeeded();const rect=await image.boundingBox();
                await page.mouse.move(rect.x+rect.width/2,rect.y+rect.height/2);await page.mouse.down();await page.mouse.move(rect.x+rect.width/2+8,rect.y+rect.height/2,{steps:3});await page.mouse.up();
                const moved=await type.locator('.notificationStudioBoard [data-layer=image]').evaluate(node=>node.style.left);
                await type.locator('.notificationStudioBoard [data-layer=image]').press('Control+z');
                assert.notEqual(await type.locator('.notificationStudioBoard [data-layer=image]').evaluate(node=>node.style.left),moved);
                await type.locator('.notificationStudioBoard [data-layer=image]').press('Control+y');
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=image]').evaluate(node=>node.style.left),moved);
                await type.locator('.notificationStudioBoard [data-layer=image]').press('Control+z');
                await type.locator('.notificationStudioBoard [data-layer=title]').press('Enter');
                await type.getByLabel('Left (%)',{exact:true}).fill('30');await type.getByLabel('Left (%)',{exact:true}).press('Tab');
                const snapDrag=async alt=>{
                    const layer=type.locator('.notificationStudioBoard [data-layer=title]');await layer.evaluate(node=>node.scrollIntoView({block:'center'}));
                    const dimensions=await layer.evaluate(node=>({left:parseFloat(node.style.left),width:parseFloat(node.style.width),canvas:node.parentElement.getBoundingClientRect().width})),bounds=await layer.boundingBox();
                    if(alt)await page.keyboard.down('Alt');
                    await page.mouse.move(bounds.x+bounds.width/2,bounds.y+bounds.height/2);await page.mouse.down();
                    await page.mouse.move(bounds.x+bounds.width/2+((100-dimensions.width)/2-dimensions.left)*dimensions.canvas/100+2,bounds.y+bounds.height/2,{steps:4});
                    if(alt){assert.equal(await type.locator('.notificationStudioGuide').count(),0);assert.notEqual(await layer.evaluate(node=>parseFloat(node.style.left)),(100-dimensions.width)/2);}
                    else{await type.locator('.notificationStudioGuide[data-axis=x][data-kind=center]').waitFor();assert.equal(await layer.evaluate(node=>parseFloat(node.style.left)+parseFloat(node.style.width)/2),50);if(process.env.ADMIN_SCREENSHOTS)await type.locator('.notificationStudioBoard').screenshot({path:process.env.ADMIN_SCREENSHOTS+'/notification-studio-guides-'+theme+'-'+width+'.png'});}
                    await page.mouse.up();if(alt)await page.keyboard.up('Alt');
                    assert.equal(await type.locator('.notificationStudioGuide').count(),0);await type.locator('.notificationStudioBoard [data-layer=title]').press('Control+z');
                };
                await snapDrag(false);await snapDrag(true);
                const beforeResize=await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>node.style.width);
                const handle=type.getByRole('button',{name:'Resize selected layer',exact:true});await handle.scrollIntoViewIfNeeded();const corner=await handle.boundingBox();
                await page.mouse.move(corner.x+corner.width/2,corner.y+corner.height/2);await page.mouse.down();await page.mouse.move(corner.x+corner.width/2-8,corner.y+corner.height/2-4,{steps:3});await page.mouse.up();
                assert.notEqual(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>node.style.width),beforeResize,'Resize handle must edit actual geometry');
                await type.locator('.notificationStudioBoard [data-layer=title]').press('Control+z');
                assert.equal(await type.locator('.notificationStudioBoard [data-layer=title]').evaluate(node=>node.style.width),beforeResize);
                assert(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth+2),'Studio overflow');
                assert(await type.locator('.notificationStudioWorkspace').evaluate(node=>node.scrollWidth<=node.clientWidth+2),'Studio workspace overflow');
                assert.equal(await type.locator('.notificationStudioResize').count(),1,'Selected layer needs an accessible resize control');
                assert(await type.locator('.notificationStudioFields .inputContainer, .notificationStudioFields .selectContainer').evaluateAll(nodes=>nodes.every(node=>getComputedStyle(node).marginTop==='0px'&&getComputedStyle(node).marginBottom==='0px')),'Native settings margins must not turn the inspector into a long form');
                await type.locator('.notificationStudioInspector').evaluate(node=>{node.scrollTop=0;});
                if(process.env.ADMIN_SCREENSHOTS)await type.screenshot({path:process.env.ADMIN_SCREENSHOTS+'/notification-studio-'+theme+'-'+width+'.png'});
                checks.push({theme,width,simulated:true,keyboard:true,shortcuts:true,drag:true,resize:true,snapping:true,alignment:true,altBypass:true,fonts:true,inspector:true,nativeTabOrder:true,noOverflow:true});
            }
        }
        phase='navigation-draft';let navigationWrites=0;const inspectNavigation=request=>{if(request.method()==='PUT'&&/\/types\/(layout|defaults)$/.test(request.url()))navigationWrites++;};page.on('request',inspectNavigation);
        const draftState=await type.locator('.notificationStudio').evaluate(node=>{node.dataset.navigationProbe='retained';return node.dataset.dirty;});
        await registeredType.locator(':scope > summary').click();await plugin.locator(':scope > summary').click();
        await plugin.locator(':scope > summary').click();await registeredType.locator(':scope > summary').click();
        assert.equal(await type.locator('.notificationStudio').getAttribute('data-navigation-probe'),'retained','Collapsing navigation must not remount the studio');
        assert.equal(await type.locator('.notificationStudio').getAttribute('data-dirty'),draftState);assert.equal(navigationWrites,0,'Catalog navigation must not save settings');page.off('request',inspectNavigation);
        phase='validation';let invalidWrites=0;const inspect=request=>{if(request.method()==='PUT'&&request.url().endsWith('/types/layout'))invalidWrites++;};page.on('request',inspect);
        await type.getByLabel('Card height',{exact:true}).fill('0');await type.getByLabel('Card height',{exact:true}).press('Tab');
        await type.getByRole('button',{name:'Save layout',exact:true}).click();
        await page.getByRole('status').filter({hasText:'Check the studio fields before saving the layout.'}).waitFor();
        assert.equal(invalidWrites,0,'Invalid inspector fields must not write a layout');page.off('request',inspect);
        await type.getByLabel('Card height',{exact:true}).fill('180');await type.getByLabel('Card height',{exact:true}).press('Tab');
        phase='save';await type.getByLabel('Default priority',{exact:true}).selectOption('low');
        const defaultsSaved=page.waitForResponse(r=>r.url().endsWith('/types/defaults')&&r.request().method()==='PUT');
        await type.getByRole('button',{name:'Save defaults',exact:true}).click();assert.equal((await defaultsSaved).status(),200);
        const layoutSaved=page.waitForResponse(r=>r.url().endsWith('/types/layout')&&r.request().method()==='PUT');
        await type.getByRole('button',{name:'Save layout',exact:true}).click();assert.equal((await layoutSaved).status(),200);
        await page.waitForFunction(()=>document.querySelector('.notificationStudio')?.dataset.dirty==='false');
        const catalogs=(await request('settings/notifications/types')).catalogs;
        const saved=catalogs.find(c=>c.source==='notifications').types.find(t=>t.type==='library.movie.added');
        assert.equal(saved.defaults.importance,'low');assert(saved.layout.canvas.layers.some(l=>l.kind==='title'&&l.fontFamily==='serif'));
        assert.equal((await request('notifications?limit=100')).notifications.length,initial.notifications.length,'Preview must not publish');
        phase='reload';await page.reload();await page.getByRole('heading',{name:'Library events',exact:true}).waitFor();
        await page.locator('[data-account-hub-bell]').click();
        await page.locator('#account-hub-inbox .notificationCanvas').first().waitFor();
        assert(await page.locator('#account-hub-inbox .notificationCanvas [data-layer=title]').first().evaluate(node=>node.style.fontFamily==='serif'));
        phase='live-poster';
        // Retry only structural readiness (connected, loaded, non-zero boxes).
        // A real overlap or wrong CSS returns a measurement immediately and
        // still fails the strict assertion below; it is never waited away.
        const sample=await page.waitForFunction(measureLivePoster);
        let placement;try{placement=await sample.jsonValue();}finally{await sample.dispose();}
        assert.deepEqual(placement,{posterLeft:true,noOverlap:true,fillsPoster:true,fit:'contain',float:'none',timestampBelow:true});
        assert.deepEqual(errors,[],'Uncaught page errors: '+JSON.stringify({errors,failedRequests}));
        // Inbox refreshes may replace a row while Playwright waits for an element
        // screenshot to stabilize. Capture the viewport, not a stale row handle.
        if(process.env.ADMIN_SCREENSHOTS)await page.screenshot({path:process.env.ADMIN_SCREENSHOTS+'/notification-live-poster.png'});
        console.log(JSON.stringify({checks,pluginGroups:registeredCatalogs.length,reportedTypes:registeredCatalogs.reduce((total,catalog)=>total+catalog.types.length,0),navigationPreservesDraft:true,persisted:true,liveBell:true,livePoster:placement,noSends:true}));
    } finally { await browser.close(); await relay.close(); }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
