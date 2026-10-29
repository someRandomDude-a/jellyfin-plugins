// Native HTTP/browser signup in an owned disposable fixture only; no Discord or .env.
const assert=require('node:assert/strict');
const {randomUUID}=require('node:crypto');
const {writeFile}=require('node:fs/promises');
const {chromium}=require('playwright');
const {openBrowserRelay}=require('./browser-relay.cjs');
(async()=>{
    assert.equal(process.env.DISPOSABLE_THEME_TEST,'true');const upstream=new URL(process.env.THEME_TEST_BASE_URL);assert(['127.0.0.1','localhost'].includes(upstream.hostname));
    const relay=await openBrowserRelay(upstream),browser=await chromium.launch({headless:true});let phase='admin-login';
    try{
        const context=await browser.newContext(),admin=await context.newPage(),errors=[];const track=page=>page.on('pageerror',error=>errors.push({phase,name:error.name}));track(admin);
        const base=relay.baseUrl;await admin.goto(base+'/web/index.html#/configurationpage?name=AccountHub');await admin.locator('#txtManualName').fill('suite-admin');await admin.locator('#txtManualPassword').fill('Disposable-Only-Suite-9714!');await admin.locator('#txtManualPassword').press('Enter');await admin.getByRole('tab',{name:'People',exact:true}).waitFor();
        const api=(path,method='GET',body)=>admin.evaluate(async({path,method,body})=>{const response=await fetch(ApiClient.getUrl('AccountHub/'+path),{method,credentials:'omit',cache:'no-store',headers:{Authorization:'MediaBrowser Token="'+ApiClient.accessToken()+'"',...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined});const result=await response.json();if(!response.ok||!result.ok)throw Error('Disposable onboarding administration rejected request');return result.data;},{path,method,body});
        const command=async extra=>({commandId:randomUUID(),revision:(await api('api/v1/workspace')).revision,confirmation:'CONFIRM ACCOUNT HUB CHANGE',...extra});
        phase='issue-approval-only-invite';const created=await api('api/v1/invites','POST',await command({maxUses:1,expiresInMinutes:30,note:'Disposable native browser signup'}));
        const issued=await api('api/v1/invites/'+created.id+'/approve','POST',await command({}));const hash=new URL(issued.oneTimeLink,base).hash;assert.match(hash,/^#token=[A-Za-z0-9_-]{43}$/);const link=base+'/AccountHub/invite'+hash;
        phase='existing-native-session';const signed=await context.newPage();track(signed);await signed.goto(link);await signed.locator('#invite-status').filter({hasText:/already signed in/}).waitFor();assert(await signed.locator('#invite-form').isHidden());assert.equal((await api('api/v1/invites/'+created.id)).reservedUses,0);await signed.close();
        phase='signed-out-native-layout';const fresh=await browser.newContext(),page=await fresh.newPage();track(page);await page.goto(link);await page.locator('#invite-form').waitFor();assert.equal(new URL(page.url()).hash,'');
        assert(await page.locator('#loginPage.standalonePage.backdropPage').count(),'Signup must use the installed native login page structure');
        assert(await page.locator('.margin-auto-y form.manualLoginForm.margin-auto-x').count(),'Signup must use native login form placement');
        assert.equal(await page.locator('html').getAttribute('dir'),'ltr','Native directional padding must be active for the English public page');
        assert(await page.locator('#loginPage > .padded-left').evaluate(node=>parseFloat(getComputedStyle(node).paddingLeft)>0),'Native safe-area padding must separate the form from screen edges');
        const checks=[];
        for(const theme of ['dark','light']){
            await page.evaluate(async theme=>{document.documentElement.classList.remove('theme-dark','theme-light');document.documentElement.classList.add('theme-'+theme);document.documentElement.dataset.theme=theme;for(const link of document.querySelectorAll('link[href*="/themes/"]'))link.remove();const link=document.createElement('link');link.rel='stylesheet';link.href=new URL('../web/themes/'+theme+'/theme.css',document.baseURI).href;await new Promise((resolve,reject)=>{link.onload=resolve;link.onerror=reject;document.head.append(link);});},theme);
            for(const width of [320,1280]){
                await page.setViewportSize({width,height:1000});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth+2));assert.equal(await page.locator('#invite-form input').evaluateAll(inputs=>inputs.filter(input=>input.labels.length!==1).length),0);
                const paint=await page.locator('#loginPage').evaluate(root=>[...root.querySelectorAll('h1,label,input,button')].map(node=>{const rect=node.getBoundingClientRect(),hit=document.elementFromPoint(rect.left+rect.width/2,rect.top+rect.height/2);let visible=true;for(let parent=node;parent;parent=parent.parentElement){const style=getComputedStyle(parent);if(style.visibility==='hidden'||style.display==='none'||Number(style.opacity)===0)visible=false;}return{tag:node.tagName,id:node.id,visible,receivesPointer:!!hit&&(node===hit||node.contains(hit)),occludingTag:hit?.tagName,occludingClass:hit?.className};}));
                assert(paint.every(node=>node.visible&&node.receivesPointer),'Native signup fields/headings must be painted above the background, not merely programmatically fillable: '+JSON.stringify(paint));
                if(process.env.ADMIN_SCREENSHOTS)await page.screenshot({path:process.env.ADMIN_SCREENSHOTS+'/signup-'+theme+'-'+width+'.png'});checks.push({theme,width,paint,passed:true});
            }
        }
        phase='safe-pre-reservation-validation';const password='Disposable-Public-Only-8319!';await page.locator('#invite-username').fill('bad/name');await page.locator('#invite-password').fill(password);await page.locator('#invite-confirm').fill(password);
        const denied=page.waitForResponse(response=>response.url().endsWith('/AccountHub/public/v1/invites/redeem')&&response.request().method()==='POST');await page.getByRole('button',{name:'Create my account',exact:true}).click();assert.equal((await denied).status(),400);await page.locator('#invite-status').filter({hasText:/Invalid or unavailable username/}).waitFor();assert.equal((await api('api/v1/invites/'+created.id)).reservedUses,0);
        phase='signup-and-native-login-redirect';const username='public-onboard-'+randomUUID().slice(0,8);await page.locator('#invite-username').fill(username);await page.locator('#invite-password').fill(password);await page.locator('#invite-confirm').fill(password);await page.locator('#invite-form button[type=submit]').click();await page.waitForURL(url=>url.pathname.endsWith('/web/index.html')&&url.hash==='#/login');await page.locator('#txtManualName').waitFor();assert.doesNotMatch(await page.locator('body').textContent(),/Set up 2FA|Jellyfin Security setup/);
        await page.locator('#txtManualName').fill(username);await page.locator('#txtManualPassword').fill(password);await page.locator('#txtManualPassword').press('Enter');await page.waitForFunction(()=>!!window.ApiClient?.accessToken?.());
        const policy=await page.evaluate(async()=>{const response=await fetch(ApiClient.getUrl('Users/Me'),{credentials:'omit',headers:{Authorization:'MediaBrowser Token="'+ApiClient.accessToken()+'"'}});if(!response.ok)throw Error('New disposable account cannot sign in');const user=await response.json();const policy=user.Policy||user.policy;return{administrator:policy.IsAdministrator??policy.isAdministrator,disabled:policy.IsDisabled??policy.isDisabled,hidden:policy.IsHidden??policy.isHidden};});assert.equal(policy.administrator,false);assert.equal(policy.disabled,false);assert.equal(policy.hidden,true);
        phase='used-invitation-denial';const other=await browser.newContext(),expired=await other.newPage();track(expired);await expired.goto(link);await expired.locator('#invite-status').filter({hasText:/unavailable|expired|fully used/}).waitFor();assert(await expired.locator('#invite-form').isHidden());const detail=await api('api/v1/invites/'+created.id);assert.equal(detail.completedUses,1);assert.equal(detail.reservedUses,1);assert(!JSON.stringify(detail).includes(hash.slice(7)));
        assert.equal(errors.length,0,'Native signup must not suppress page errors');
        if(process.env.ADMIN_SCREENSHOTS)await writeFile(process.env.ADMIN_SCREENSHOTS+'/signup-results.json',JSON.stringify({checks,existingSessionDetected:true,invalidUsernameBeforeReservation:true,nativeLoginRedirect:true,nativeSignIn:true,usedInvitationDenied:true,pageErrors:0},null,2));
        console.log('PASS native invitation UI: existing-session detection without redemption, four native theme/width cases, safe invalid-username validation before reservation, native login redirect/sign-in and used-link denial.');
    }catch(error){
        const message=String(error.message||'').replaceAll('Disposable-Only-Suite-9714!','[withheld]').replaceAll('Disposable-Public-Only-8319!','[withheld]').replace(/#token=[A-Za-z0-9_-]+/g,'#token=[withheld]').replace(/https?:\/\/[^\s)]+/g,value=>{try{const url=new URL(value);return url.origin+url.pathname;}catch{return '[local page]';}});
        if(process.env.ADMIN_SCREENSHOTS)await writeFile(process.env.ADMIN_SCREENSHOTS+'/signup-failure.json',JSON.stringify({phase,name:error.name,message},null,2));
        console.error('Public onboarding acceptance failed at '+phase+' ('+error.name+'); sanitized evidence saved.');process.exitCode=1;
    }
    finally{await browser.close();await relay.close();}
})();
