const test=require('node:test');
const assert=require('node:assert/strict');
const {chromium}=require('playwright');
const {measureLivePoster}=require('./live-poster-measurement.cjs');

test('Chromium measures the refreshed poster and exposes real overlap and CSS failures',async()=>{
    const browser=await chromium.launch({headless:true});
    try{
        const page=await browser.newPage();
        await page.setContent(`<style>
            .notificationCanvas{position:relative;width:500px;height:180px}
            .notificationCanvasLayer{position:absolute;left:120px;top:0;width:200px;height:30px}
            .notificationCanvasImage{left:0;width:100px;height:160px}
            img{width:100%;height:100%;object-fit:contain;float:none}
            time{display:block;width:500px;height:20px}
            </style><div id="account-hub-inbox"><div class="notificationMainCanvas"><div class="notificationCanvas"><div class="notificationCanvasLayer notificationCanvasImage"><img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='160'/%3E"></div><div class="notificationCanvasLayer title">Title</div></div><time>Now</time></div></div>`);
        const stale=await page.$('.notificationMainCanvas');
        await page.evaluate(()=>{const old=document.querySelector('.notificationMainCanvas');old.replaceWith(old.cloneNode(true));});
        const detached=await stale.evaluate(main=>{
            const image=main.querySelector('img'),style=getComputedStyle(image),poster=main.querySelector('.notificationCanvasImage').getBoundingClientRect(),text=main.querySelector('.title').getBoundingClientRect();
            return{connected:main.isConnected,fit:style.objectFit,float:style.float,noOverlap:poster.right<text.left};
        });
        assert.deepEqual(detached,{connected:false,fit:'',float:'',noOverlap:false},'Detached Chromium geometry and styles reproduce the reported failure');
        const sample=await page.waitForFunction(measureLivePoster);
        assert.deepEqual(await sample.jsonValue(),{posterLeft:true,noOverlap:true,fillsPoster:true,fit:'contain',float:'none',timestampBelow:true});await sample.dispose();await stale.dispose();
        await page.locator('.title').evaluate(node=>{node.style.left='0px';});
        const overlap=await page.waitForFunction(measureLivePoster);assert.equal((await overlap.jsonValue()).noOverlap,false);await overlap.dispose();
        await page.locator('.title').evaluate(node=>{node.style.left='120px';});
        await page.locator('img').evaluate(node=>{node.style.objectFit='cover';node.style.cssFloat='left';});
        const wrongCss=await page.waitForFunction(measureLivePoster),styles=await wrongCss.jsonValue();
        assert.equal(styles.fit,'cover');assert.equal(styles.float,'left');await wrongCss.dispose();
    }finally{await browser.close();}
});
