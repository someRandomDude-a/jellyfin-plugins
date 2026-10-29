const assert=require('node:assert/strict');
const {chromium}=require('playwright');
(async()=>{
    assert.equal(process.env.DISPOSABLE_THEME_TEST,'true');
    const base=new URL(process.env.THEME_TEST_BASE_URL);assert(['127.0.0.1','localhost'].includes(base.hostname));
    const browser=await chromium.launch({headless:true});const results=[];
    try{
        const page=await browser.newPage();const errors=[];page.on('pageerror',error=>errors.push(error.message));
        await page.goto(base.href+'/web/index.html#/configurationpage?name=DiscordBridge');
        await page.locator('#txtManualName').fill('suite-admin');await page.locator('#txtManualPassword').fill('Disposable-Only-Suite-9714!');await page.locator('#txtManualPassword').press('Enter');
        await page.locator('.bridgeSettings').waitFor();
        async function check(kind,theme,width){
            await page.setViewportSize({width,height:900});
            await page.evaluate(async theme=>{document.documentElement.dataset.theme=theme;for(const link of document.querySelectorAll('link[data-responsive-theme]'))link.remove();const link=document.createElement('link');link.dataset.responsiveTheme='true';link.rel='stylesheet';link.href=location.origin+'/suite/web/themes/'+theme+'/theme.css';await new Promise((resolve,reject)=>{link.onload=resolve;link.onerror=reject;document.head.append(link);});},theme);
            const root=page.locator(kind==='discord'?'.discordBridgePage':'.accountHubPublic');
            const layout=await root.evaluate(root=>({overflow:document.documentElement.scrollWidth>innerWidth+2,rootOverflow:root.scrollWidth>root.clientWidth+2,unlabeled:[...root.querySelectorAll('input:not([type=hidden]),select')].filter(e=>!e.labels?.length).length,fields:[...root.querySelectorAll('input:not([type=hidden]):not([type=checkbox]),select')].every(e=>e.getBoundingClientRect().width<=root.getBoundingClientRect().width+1),columns:root.querySelector('.bridgeFieldGrid')?getComputedStyle(root.querySelector('.bridgeFieldGrid')).gridTemplateColumns.split(' ').length:null}));
            assert(!layout.overflow&&!layout.rootOverflow&&layout.fields,kind+'/'+theme+'/'+width+' overflow: '+JSON.stringify(layout));assert.equal(layout.unlabeled,0);
            if(kind==='discord')assert.equal(layout.columns,width<=600?1:2);
            await page.addStyleTag({content:(kind==='discord'?'.discordBridgePage':'.accountHubPublic')+' h1 {color:rgb(33,91,120) !important;}'});
            assert.equal(await root.locator('h1').evaluate(e=>getComputedStyle(e).color),'rgb(33, 91, 120)');
            if(process.env.ADMIN_SCREENSHOTS)await root.screenshot({path:process.env.ADMIN_SCREENSHOTS+'/'+kind+'-'+theme+'-'+width+'.png'});
            results.push({kind,theme,width,passed:true});
        }
        for(const theme of ['dark','light'])for(const width of [320,390,1280])await check('discord',theme,width);
        await page.route('**/AccountHub/public/v1/resets/lookup',route=>route.fulfill({json:{ok:true,data:{valid:true,verifiedSessionRequired:true,verificationAvailable:true}}}));
        await page.route('**/AccountHub/public/v1/resets/session',route=>route.fulfill({json:{ok:true,data:{ready:true}}}));
        await page.goto(base.href+'/AccountHub/reset#token='+'a'.repeat(43));await page.locator('#new-password').waitFor();
        for(const theme of ['dark','light'])for(const width of [320,390,1280])await check('reset',theme,width);
        await page.route('**/AccountHub/api/v1/discord/link/lookup',route=>route.fulfill({json:{ok:true,data:{userId:'11111111-1111-1111-1111-111111111111',jellyfinUsername:'A long test account name',discordUsername:'friendly.long.username',discordId:'123456789012345678'}}}));
        await page.goto(base.href+'/AccountHub/discord/link#token='+'a'.repeat(43));
        await page.locator('.hubPublicAccounts').waitFor();
        for(const theme of ['dark','light'])for(const width of [320,390,1280])await check('link',theme,width);
        assert.deepEqual(errors,[]);console.log(JSON.stringify({checks:results}));
    }finally{await browser.close();}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
