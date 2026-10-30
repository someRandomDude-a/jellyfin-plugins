// Installed disposable fixture only. Never load .env or send external messages.
const assert=require('node:assert/strict');
const {writeFile}=require('node:fs/promises');
const {chromium}=require('playwright');
const {openBrowserRelay}=require('./browser-relay.cjs');
(async()=>{
    assert.equal(process.env.DISPOSABLE_THEME_TEST,'true');
    const upstream=new URL(process.env.THEME_TEST_BASE_URL);
    assert(['127.0.0.1','localhost'].includes(upstream.hostname));
    const relay=await openBrowserRelay(upstream),browser=await chromium.launch({headless:true});
    let phase='login';const errors=[],secrets=new Set(['Disposable-Only-Suite-9714!','Disposable-Diagnostics-Only-6328!']);
    const sanitize=text=>{let safe=String(text||'');for(const secret of secrets)if(secret)safe=safe.split(secret).join('[withheld]');return safe.replace(/https?:\/\/[^\s)]+/g,value=>{try{const url=new URL(value);return url.origin+url.pathname;}catch{return '[local script]';}});};
    const saveErrors=async()=>{if(process.env.ADMIN_SCREENSHOTS)await writeFile(process.env.ADMIN_SCREENSHOTS+'/diagnostics-page-errors.json',JSON.stringify(errors.map(error=>({...error,message:sanitize(error.message),stack:sanitize(error.stack)})),null,2));};
    try{
        const context=await browser.newContext(),page=await context.newPage();
        page.on('pageerror',error=>errors.push({phase,name:error.name,message:error.message,stack:error.stack}));
        const start=process.env.DIAGNOSTICS_START_PAGE||'Notifications',base=relay.baseUrl;
        await page.goto(base+'/web/index.html#/configurationpage?name='+start);
        await page.locator('#txtManualName').fill('suite-admin');
        await page.locator('#txtManualPassword').fill('Disposable-Only-Suite-9714!');
        await page.locator('#txtManualPassword').press('Enter');
        const panel=()=>page.locator('.suiteDiagnostics:visible');
        await panel().waitFor();
        secrets.add(await page.evaluate(()=>ApiClient.accessToken()));
        const api=(path,method='GET',body)=>page.evaluate(async({path,method,body})=>{
            const response=await fetch(ApiClient.getUrl('SuiteDiagnostics/api/v1/'+path),{method,credentials:'omit',cache:'no-store',headers:{Authorization:'MediaBrowser Token="'+ApiClient.accessToken()+'"',...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined});
            const value=await response.json();if(!response.ok||value.ok!==true)throw Error('Disposable diagnostics request rejected');return value.data;
        },{path,method,body});
        const before=await api('errors');
        const crossOwner=before.plugins.find(plugin=>plugin.page!==start)||before.plugins[0];
        const prefixes={'account-hub':'AccountHub/api/v1/workspace','notifications':'Notifications/api/v1/settings/notifications','discord-bridge':'DiscordBridge/admin/v1/settings','private-syncplay':'PrivateSyncPlay/admin/account-hub/v1/settings','deletion-queue':'DeletionQueue/admin/status','automatic-deletion-requests':'AutomaticDeletionRequests/admin'};
        phase='create-opaque-failure';
        const id=await page.evaluate(async path=>{const response=await fetch(ApiClient.getUrl(path),{credentials:'omit',cache:'no-store'});if(![401,403].includes(response.status))throw Error('Anonymous disposable request was not denied');return response.headers.get('X-Suite-Error-Id');},prefixes[crossOwner.source]);
        assert.match(id||'',/^[a-f0-9-]{36}$/i);
        phase='lookup-owner-navigation';
        await panel().locator(':scope > summary').click();
        await panel().getByLabel('Error ID',{exact:true}).fill(id);
        await panel().getByRole('button',{name:'Search errors',exact:true}).click();
        if(crossOwner.page!==start)await page.waitForURL(url=>url.hash.includes('name='+crossOwner.page)&&url.hash.includes('suiteError='+id));
        await panel().locator('[role=status]').filter({hasText:/^1 records shown\.$/}).waitFor();
        const row=panel().locator('tbody tr').filter({has:page.locator('input[data-diagnostic-id="'+id+'"]')});
        const record=row.locator('details');
        await record.waitFor();
        await record.locator(':scope > summary').click();
        assert.equal(await panel().locator('input[data-diagnostic-id]').count(),1);
        assert((await record.textContent()).includes(crossOwner.source));
        assert(await page.evaluate(()=>![...document.querySelectorAll('.suiteDiagnostics')].some(node=>node.textContent.includes(ApiClient.accessToken()))));
        const checks=[];
        phase='native-layout';
        for(const theme of ['dark','light']){
            await page.evaluate(async theme=>{document.documentElement.dataset.theme=theme;for(const link of document.querySelectorAll('link[href*="/themes/"]'))link.remove();const link=document.createElement('link');link.rel='stylesheet';link.href=new URL('themes/'+theme+'/theme.css',document.baseURI).href;await new Promise((resolve,reject)=>{link.onload=resolve;link.onerror=reject;document.head.append(link);});},theme);
            for(const width of [320,1280]){
                await page.setViewportSize({width,height:1000});
                if(!await record.evaluate(node=>node.open))await record.locator(':scope > summary').click();
                assert(await panel().evaluate(node=>node.scrollWidth<=node.clientWidth+2),'Diagnostics panel must not overflow');
                assert.equal(await panel().locator('input:not([aria-label]),select:not([aria-label])').count(),0);
                if(process.env.ADMIN_SCREENSHOTS)await panel().screenshot({path:process.env.ADMIN_SCREENSHOTS+'/diagnostics-'+theme+'-'+width+'.png'});
                checks.push({theme,width,passed:true});
            }
        }
        phase='levels-and-selected-clear';
        const current=await api('errors');
        await panel().getByText('Recording settings',{exact:true}).click();
        await panel().getByLabel('Minimum recorded diagnostic level',{exact:true}).selectOption('critical');
        const saved=page.waitForResponse(response=>response.url().endsWith('/SuiteDiagnostics/api/v1/settings')&&response.request().method()==='POST');
        await panel().getByRole('button',{name:'Save recording level',exact:true}).click();
        assert.equal((await saved).status(),200);
        await panel().locator('[role=status]').filter({hasText:/^1 records shown\.$/}).waitFor();
        assert.equal((await api('errors')).settings.minimumLevel,'critical');
        await panel().getByRole('button',{name:'Clear selected errors',exact:true}).click();
        await panel().locator('[role=status]').filter({hasText:/^Type CLEAR DIAGNOSTICS/}).waitFor();
        assert.equal((await api('errors?id='+id)).items.length,1);
        if(!await record.evaluate(node=>node.open))await record.locator(':scope > summary').click();
        await row.getByRole('checkbox',{name:'Select error '+id,exact:true}).check();
        assert.equal(await panel().locator('input[data-diagnostic-id="'+id+'"]').isChecked(),true);
        await panel().getByLabel('Type CLEAR DIAGNOSTICS to clear selected errors',{exact:true}).fill('CLEAR DIAGNOSTICS');
        await panel().getByRole('button',{name:'Clear selected errors',exact:true}).click();
        await panel().locator('[role=status]').filter({hasText:/^No matching retained records\./}).waitFor();
        assert.equal((await api('errors?id='+id)).items.length,0);
        const settings=(await api('errors')).settings;
        await api('settings','POST',{revision:settings.revision,minimumLevel:current.settings.minimumLevel});
        if(crossOwner.page!==start){
            phase='cached-owner-navigation';
            await page.goto(base+'/web/index.html#/configurationpage?name='+start);
            const rootNames={AccountHub:'account-hub',Notifications:'notifications',DiscordBridge:'discord',privateSyncPlay:'private-syncplay',DeletionQueueAdmin:'deletion-queue',AutomaticDeletionRequests:'automatic'};
            const startPanel=page.locator('[data-suite-plugin="'+rootNames[start]+'"]:visible .suiteDiagnostics');await startPanel.waitFor();
            const nextId=await page.evaluate(async path=>{const response=await fetch(ApiClient.getUrl(path),{credentials:'omit',cache:'no-store'});if(![401,403].includes(response.status))throw Error('Disposable anonymous request was not denied');return response.headers.get('X-Suite-Error-Id');},prefixes[crossOwner.source]);assert.match(nextId||'',/^[a-f0-9-]{36}$/i);assert.notEqual(nextId,id);
            if(!await startPanel.evaluate(node=>node.open))await startPanel.locator(':scope > summary').click();
            await startPanel.getByLabel('Error ID',{exact:true}).fill(nextId);await startPanel.getByRole('button',{name:'Search errors',exact:true}).click();
            await page.waitForURL(url=>url.hash.includes('name='+crossOwner.page)&&url.hash.includes('suiteError='+nextId));
            await panel().locator('[role=status]').filter({hasText:/^1 records shown\.$/}).waitFor();
            await panel().locator('input[data-diagnostic-id="'+nextId+'"]').waitFor({state:'attached'});
            assert.equal(await panel().locator('input[data-diagnostic-id]').count(),1);assert.equal(await panel().getByLabel('Error ID',{exact:true}).inputValue(),nextId);
        }
        phase='ordinary-user-denial';
        const ordinary=await browser.newContext(),member=await ordinary.newPage();
        member.on('pageerror',error=>errors.push({phase:'ordinary-user',name:error.name,message:error.message,stack:error.stack}));
        await member.goto(base+'/web/index.html#/home');
        await member.locator('#txtManualName').fill('diagnostics-ui-member');await member.locator('#txtManualPassword').fill('Disposable-Diagnostics-Only-6328!');await member.locator('#txtManualPassword').press('Enter');
        await member.waitForFunction(()=>!!window.ApiClient?.accessToken?.());
        secrets.add(await member.evaluate(()=>ApiClient.accessToken()));
        const denied=await member.evaluate(async()=>{const response=await fetch(ApiClient.getUrl('SuiteDiagnostics/api/v1/errors'),{credentials:'omit',headers:{Authorization:'MediaBrowser Token="'+ApiClient.accessToken()+'"'}});const body=await response.text();return{status:response.status,disclosed:/exceptionType|stackTrace|frames|"items"/.test(body)};});
        assert([401,403].includes(denied.status));assert.equal(denied.disclosed,false);
        await ordinary.close();
        await saveErrors();assert.equal(errors.length,0,'Do not suppress native page errors; see diagnostics-page-errors.json');
        const result={checks,ownerRedirect:crossOwner.page!==start,cachedOwnerRedirect:crossOwner.page!==start,sharedSourceCount:before.plugins.length,opaqueLookup:true,selectedClear:true,revisionedLevels:true,ordinaryUserDenied:true,pageErrors:0};
        if(process.env.ADMIN_SCREENSHOTS)await writeFile(process.env.ADMIN_SCREENSHOTS+'/diagnostics-results.json',JSON.stringify(result,null,2));
        console.log('PASS native diagnostics UI: shared lookup/owner navigation, four native theme/width cases, level control, explicit selected clear, ordinary-user detail denial and zero page errors.');
    }catch(error){await saveErrors();console.error('Diagnostics acceptance failed at '+phase+' ('+error.name+')');if(phase!=='login')console.error(sanitize(error.message));throw error;}
    finally{await browser.close();await relay.close();}
})().catch(()=>{process.exitCode=1;});
