// Run only against the explicitly retained disposable integration fixture. Never load .env.
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const http = require('node:http');
(async () => {
    assert.equal(process.env.DISPOSABLE_THEME_TEST, 'true');
    let base = new URL(process.env.THEME_TEST_BASE_URL);
    assert(['127.0.0.1', 'localhost', 'host.docker.internal'].includes(base.hostname));
    assert(!base.username && !base.password && !base.search && !base.hash);
    // A container-local loopback relay gives Web Crypto its normal localhost
    // secure context without disabling browser security or changing plugin code.
    const upstream = base;
    const relay = http.createServer((req, res) => {
        const outgoing = http.request(new URL(req.url, upstream.origin), { method: req.method, headers: { ...req.headers, host: upstream.host } }, incoming => {
            res.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(res);
        });
        outgoing.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(outgoing);
    });
    await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
    base = new URL(base.pathname, `http://127.0.0.1:${relay.address().port}`);
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage();
        await page.goto(base.href.replace(/\/$/, '') + '/web/index.html#/configurationpage?name=AccountHub');
        await page.locator('#txtManualName').fill('suite-admin');
        await page.locator('#txtManualPassword').fill('Disposable-Only-Suite-9714!');
        await page.locator('#txtManualPassword').press('Enter');
        await page.getByRole('tab', { name: 'People', exact: true }).waitFor();
        const results = [];
        for (const theme of ['dark', 'light']) {
            await page.evaluate(async theme => {
                for (const link of document.querySelectorAll('link[href*="/themes/"]')) link.remove();
                const link = document.createElement('link'); link.rel = 'stylesheet';
                link.href = new URL('themes/' + theme + '/theme.css', document.baseURI).href;
                await new Promise((resolve, reject) => { link.onload = resolve; link.onerror = reject; document.head.append(link); });
            }, theme);
            for (const width of [320, 390, 1280]) {
                await page.setViewportSize({ width, height: 900 });
                for (const name of ['People', 'Groups', 'Invites', 'Library', 'Notifications', 'Settings']) {
                    const tab = page.getByRole('tab', { name, exact: true });
                    await tab.click();
                    const panel = page.getByRole('tabpanel', { name, exact: true });
                    await page.waitForFunction(() => document.querySelector('.hubMessage')?.textContent !== 'Loading…');
                    assert.equal(await page.locator('.hubMessage').textContent(), '', `${theme}/${width}/${name}: workspace failed to load`);
                    await panel.locator(':scope > *').first().waitFor();
                    await page.waitForLoadState('networkidle');
                    const check = await panel.evaluate(panel => ({
                        overflow: panel.scrollWidth > panel.clientWidth + 2,
                        dimensions: { scroll: panel.scrollWidth, client: panel.clientWidth },
                        overflowElements: [...panel.querySelectorAll('*')].filter(e => e.scrollWidth > e.clientWidth + 2 || e.getBoundingClientRect().right > panel.getBoundingClientRect().right + 2).map(e => ({ tag: e.tagName, classes: e.className, width: Math.round(e.getBoundingClientRect().width), scroll: e.scrollWidth, overflowX: getComputedStyle(e).overflowX })).slice(0, 12),
                        unlabeled: [...panel.querySelectorAll('input:not([type=hidden]),select,textarea')].filter(e => !e.labels?.length && !e.getAttribute('aria-label') && !e.getAttribute('aria-labelledby')).length,
                        badCheckboxes: [...panel.querySelectorAll('input[type=checkbox]')].filter(e => !e.classList.contains('emby-checkbox') || !e.closest('label')?.querySelector('.checkboxOutline')).length
                    }));
                    assert.equal(check.overflow, false, `${theme}/${width}/${name}: panel overflows ${JSON.stringify(check.dimensions)} ${JSON.stringify(check.overflowElements)}`);
                    assert.equal(check.unlabeled, 0, `${theme}/${width}/${name}: unlabeled controls`);
                    assert.equal(check.badCheckboxes, 0, `${theme}/${width}/${name}: non-native checkbox`);
                    results.push({ theme, width, tab: name, passed: true });
                }
            }
        }
        const people = page.getByRole('tab', { name: 'People', exact: true });
        await people.focus(); await people.press('Home');
        await page.waitForFunction(() => document.activeElement?.textContent === 'People' && document.activeElement?.getAttribute('aria-selected') === 'true');
        await people.press('ArrowRight');
        await page.waitForFunction(() => document.activeElement?.textContent === 'Groups' && document.activeElement?.getAttribute('aria-selected') === 'true');
        await page.getByRole('tab', { name: 'Groups', exact: true }).press('End');
        await page.waitForFunction(() => document.activeElement?.textContent === 'Settings' && document.activeElement?.getAttribute('aria-selected') === 'true');
        // Native themes themselves use important foreground rules. A custom
        // theme can override these without plugin-owned inline presentation.
        await page.addStyleTag({ content: '.accountHubPage .hubTabs [role="tab"] { color: rgb(18, 98, 136) !important; }' });
        await page.waitForFunction(() => getComputedStyle([...document.querySelectorAll('[role="tab"]')].find(e => e.textContent === 'People')).color === 'rgb(18, 98, 136)');
        const color = await page.getByRole('tab', { name: 'People', exact: true }).evaluate(e => getComputedStyle(e).color);
        assert.equal(color, 'rgb(18, 98, 136)', 'Custom theme must own native button presentation');
        await page.goto(base.href.replace(/\/$/, '') + '/web/index.html#/configurationpage?name=privateSyncPlay');
        await page.waitForFunction(() => document.querySelector('#pspSave') || document.querySelector('#pspForm')?.textContent.includes('Could not load settings'));
        assert.equal(await page.locator('#pspSave').count(), 1, 'Private SyncPlay settings must load: ' + await page.locator('#pspForm').textContent());
        const deletionUi = await page.evaluate(() => ({
            injected: !!document.querySelector('#dq-web-client'), loaded: !!window.__deletionQueueWeb,
            controls: document.querySelectorAll('.dq-bell, .dq-local-shortcut').length,
            serverAddressType: typeof window.ApiClient?.serverAddress,
            userMethod: typeof window.ApiClient?.getCurrentUserId,
            headers: document.querySelectorAll('.headerRight, .MuiToolbar-root').length
        }));
        assert(deletionUi.controls > 0, 'Deletion Queue toolbar integration missing: ' + JSON.stringify(deletionUi));
        // Installed injection + bounded DOM/API contracts; no deletion or real media access.
        const deletionItem = '11111111111111111111111111111111';
        let deletionPreviews = 0;
        await page.route('**/DeletionQueue/targets/items/*/capabilities', route => route.fulfill({ json: { canPropose: true } }));
        await page.route('**/DeletionQueue/targets/items/*/preview', route => {
            deletionPreviews++;
            return route.fulfill({ json: { title: 'Deletion UI fixture', kind: 'Movie', executionAvailable: false } });
        });
        await page.evaluate(id => {
            history.replaceState(null, '', '#/details?id=' + id);
            const fixture = document.createElement('section'); fixture.id = 'dq-installed-contract';
            fixture.innerHTML = `<div class="mainDetailButtons"></div><div class="card" data-type="Movie" data-id="${id}"><div class="cardScalable">Fixture</div></div><div class="je-request-card"><div class="je-request-info"><span data-media-id="${id}">Enhanced available request fixture</span><div class="je-request-actions"></div></div></div>`;
            document.body.append(fixture);
        }, deletionItem);
        for (const selector of ['#dq-installed-contract .mainDetailButtons .dq-media-action', '#dq-installed-contract .dq-request-action']) {
            await page.locator(selector).waitFor({ state: 'attached' });
            await page.locator(selector).dispatchEvent('click');
            await page.getByRole('heading', { name: 'Deletion UI fixture', exact: true }).waitFor();
            await page.locator('dialog.dq-panel').getByRole('button', { name: 'Close', exact: true }).click();
        }
        await page.locator('#dq-installed-contract .card').dispatchEvent('contextmenu');
        await page.evaluate(() => {
            const menu = document.createElement('div'); menu.className = 'actionSheet';
            menu.innerHTML = '<div class="actionSheetScroller"></div>';
            menu.addEventListener('click', () => { menu.dispatchEvent(new Event('close')); menu.remove(); });
            document.body.append(menu);
        });
        await page.locator('.dq-context-action').waitFor({ state: 'attached' });
        await page.locator('.dq-context-action').dispatchEvent('click');
        await page.getByRole('heading', { name: 'Deletion UI fixture', exact: true }).waitFor();
        await page.locator('dialog.dq-panel').getByRole('button', { name: 'Close', exact: true }).click();
        assert.equal(deletionPreviews, 3, 'Detail, Enhanced request and right-click must each open a preview without proposing deletion');
        await page.goto(base.href.replace(/\/$/, '') + '/DiscordBridge/activity/');
        await page.getByRole('heading', { name: 'Jellyfin watch-party companion', exact: true }).waitFor();
        await page.waitForFunction(() => document.querySelector('[data-status]')?.textContent.includes('Activity unavailable'));
        const activity = [];
        for (const width of [320, 390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            const input = page.getByLabel('One-use watch-party invitation', { exact: true });
            await input.waitFor();
            assert(await page.getByRole('button', { name: 'Open Jellyfin', exact: true }).isDisabled());
            assert(await page.getByRole('button', { name: 'Open invitation in Jellyfin', exact: true }).isDisabled());
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2), `Activity/${width}: page overflows`);
            activity.push({ width, unavailableFailsClosed: true, nativeLabels: true });
        }
        console.log(JSON.stringify({ checks: results, customThemeOverride: true, keyboardTabNavigation: true, syncplaySettings: true, activity }));
    } finally { await browser.close(); relay.closeAllConnections(); await new Promise(resolve => relay.close(resolve)); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
