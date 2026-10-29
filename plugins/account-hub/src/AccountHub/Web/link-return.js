/* Same-tab native sign-in continuation. The URL marker is not a credential. */
(()=>{
    'use strict';
    if(window.__accountHubLinkReturn)return;window.__accountHubLinkReturn=true;
    const server=new URL('../../',document.currentScript.src);
    let busy=false,finished=false,timer,started;
    // Native Security can return to plain /web after verification, dropping the
    // native-login route's url parameter. Retain only short-lived intent, never
    // a link challenge, user identity, cookie nonce or authentication credential.
    const intentKey='accountHub.linkReturnIntent:'+server.pathname;
    function clearIntent(){try{sessionStorage.removeItem(intentKey);}catch{}}
    function intent(){try{const expiry=Number(sessionStorage.getItem(intentKey));if(Number.isFinite(expiry)&&expiry>Date.now()&&expiry<=Date.now()+300000)return true;clearIntent();}catch{}return false;}
    function marked(){
        const query=new URLSearchParams(location.hash.split('?')[1]||''),direct=query.get('accountHubLinkReturn')==='1';
        const login=/^#\/?login(?:\?|$)/i.test(location.hash);
        if((direct||login&&query.get('url')==='home?accountHubLinkReturn=1')&&!intent())try{sessionStorage.setItem(intentKey,String(Date.now()+300000));}catch{}
        // A previously rejected native token on the login screen must not consume
        // the transport before the native router completes the new sign-in.
        return !login&&(direct||intent());
    }
    function fail(id){
        finished=true;clearIntent();clearTimeout(timer);clearInterval(watch);
        const status=document.createElement('p');status.className='infoBanner';status.setAttribute('role','alert');
        status.textContent='This account connection could not be resumed. Reopen the original link to review it.'+(typeof id==='string'&&/^[a-f0-9-]{36}$/i.test(id)?' Error ID: '+id:'');
        document.body.append(status);
    }
    async function resume(){
        if(finished||busy||!marked())return;
        started??=Date.now();if(Date.now()-started>60000){fail();return;}
        const client=window.ApiClient,token=client?.accessToken?.();
        if(!token){clearTimeout(timer);timer=setTimeout(()=>void resume(),250);return;}
        try{
            const address=new URL(client.serverAddress(),location.href);
            if(address.href.replace(/\/+$/,'')!==server.href.replace(/\/+$/,'')||typeof token!=='string'||!/^[A-Za-z0-9._~+/=-]{1,4096}$/.test(token)){fail();return;}
        }catch{fail();return;}
        busy=true;const controller=new AbortController(),deadline=setTimeout(()=>controller.abort(),15000);
        try{
            const response=await fetch(new URL('AccountHub/api/v1/discord/link/continuation',server),{method:'POST',credentials:'same-origin',cache:'no-store',referrerPolicy:'no-referrer',signal:controller.signal,headers:{Authorization:'MediaBrowser Token="'+token+'"','Content-Type':'application/json'},body:'{}'});
            const result=await response.json().catch(()=>({}));
            if(!marked()||token!==window.ApiClient?.accessToken?.()){finished=true;clearIntent();clearInterval(watch);return;}
            // Only an explicit native pre-effect factor denial may be checked
            // again. Network/unknown outcomes never replay the one-use handoff.
            if([401,403].includes(response.status)&&result.twoFactorRequired===true){timer=setTimeout(()=>void resume(),1000);return;}
            if(!response.ok||result.ok!==true||!/[A-Za-z0-9_-]{43}/.test(result.data?.token||'')||result.data.token.length!==43){fail(result.error?.errorId||result.errorId||response.headers.get('X-Suite-Error-Id'));return;}
            finished=true;clearIntent();clearInterval(watch);location.replace(new URL('AccountHub/discord/link#token='+result.data.token,server).href);
        }catch{fail();}
        finally{clearTimeout(deadline);busy=false;}
    }
    const check=()=>{if(!marked()){clearTimeout(timer);started=undefined;return;}void resume();};
    // Native's SPA router can use history.pushState without a hashchange event.
    // The timer checks only the credential-free marker; it never retries a request.
    const watch=setInterval(check,500);
    addEventListener('hashchange',check);addEventListener('popstate',check);addEventListener('focus',check);
    // Jellyfin also bubbles a CustomEvent named pagehide for SPA views; only
    // a real browser page transition should stop the return helper.
    addEventListener('pagehide',event=>{if(!(event instanceof PageTransitionEvent))return;finished=true;clearTimeout(timer);clearInterval(watch);});
    check();
})();
