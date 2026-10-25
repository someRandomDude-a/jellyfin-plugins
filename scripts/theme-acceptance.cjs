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
        // Preserve the browser Host as well as Origin. Replacing Host with the
        // upstream port makes native same-origin checks reject legitimate writes.
        const outgoing = http.request(new URL(req.url, upstream.origin), { method: req.method, headers: { ...req.headers } }, incoming => {
            res.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(res);
        });
        outgoing.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(outgoing);
    });
    await new Promise(resolve => relay.listen(0, '127.0.0.1', resolve));
    base = new URL(base.pathname, `http://127.0.0.1:${relay.address().port}`);
    const browser = await chromium.launch({ headless: true });
    let phase = 'login';
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
                    phase = `workspace/${theme}/${width}/${name}`;
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
        const ownsNotifications = await page.locator('[data-notifications-bootstrap]').count() > 0;
        if (ownsNotifications) {
            assert.equal(await page.locator('[data-notifications-bell]').count(), 1, 'Standalone plugin must own exactly one bell');
            assert.equal(await page.locator('.dq-bell').count(), 0, 'Deletion Queue must not mount a competing bell');
            await page.locator('[data-notifications-bell]').click();
            await page.locator('#account-hub-inbox').waitFor({ state: 'visible' });
            await page.getByRole('button', { name: 'Notification settings', exact: true }).click();
            await page.getByRole('heading', { name: 'Notification settings', exact: true }).waitFor();
            const muteInApp = page.getByRole('button', { name: 'Mute non-critical in-app notifications', exact: true });
            await muteInApp.click();
            await page.waitForFunction(() => document.querySelector('[aria-label="Mute non-critical in-app notifications"]')?.getAttribute('aria-pressed') === 'true');
            await page.getByText('Muted sources and types', { exact: true }).click();
            await page.getByRole('button', { name: 'Unmute * · * · in-app', exact: true }).click();
            await page.waitForFunction(() => document.querySelector('[aria-label="Enable non-critical in-app notifications"]')?.getAttribute('aria-pressed') === 'true');
            await page.getByRole('button', { name: 'Close notifications', exact: true }).click();
            const inboxPattern = '**/Notifications/api/v1/notifications?*';
            const actionPattern = '**/Notifications/api/v1/notifications/*/actions';
            let selectedAnswer = 'allow', inlineActions = 0;
            await page.route(actionPattern, async route => {
                const command = route.request().postDataJSON(); assert(command.commandId && command.snapshot === 'fixture-snapshot');
                selectedAnswer = command.id; inlineActions++; await route.fulfill({ json: { ok: true, data: { status: 'updated' } } });
            });
            await page.route(inboxPattern, route => {
                const skip = Number(new URL(route.request().url()).searchParams.get('skip') || 0);
                return route.fulfill({ json: { ok: true, data: { hasMore: skip === 0, notifications: Array.from({ length: 20 }, (_, i) => ({ id: 'fixture-' + (skip + i), read: false, event: {
                    title: 'Compact notification ' + (skip + i), body: 'A short summary of the update.', details: 'Expanded details stay inside the notification menu.',
                    occurredAt: new Date(Date.now() - (i === 19 ? 8 * 86400000 : 2 * 3600000)).toISOString(), severity: i % 10 === 0 ? 'warning' : 'info',
                    actions: [{ label: 'Small action', link: '#/home', size: 'compact' }, { label: 'Normal action', link: '#/home', size: 'normal' }]
                    ,commands: ['allow','keep','undo'].map(id => ({ id, label: id[0].toUpperCase() + id.slice(1), revision: 1, snapshot: 'fixture-snapshot', enabled: true, selected: id === selectedAnswer }))
                } })) } } });
            });
            for (const width of [320, 1280]) {
                phase = 'notification-scroll/' + width;
                await page.setViewportSize({ width, height: 900 });
                await page.locator('[data-notifications-bell]').click();
                await page.locator('#account-hub-inbox section').filter({ hasText: 'Compact notification 0' }).waitFor();
                const inbox = page.locator('#account-hub-inbox');
                assert(await inbox.evaluate(e => { const r = e.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth + 1 && r.bottom <= innerHeight; }), 'Notification menu must fit the viewport');
                if (width > 600) assert(await inbox.evaluate(e => { const r = e.getBoundingClientRect(); return r.height <= innerHeight / 2 + 1 && Math.abs(innerWidth - r.right - 12) <= 2; }), 'Desktop inbox must be half-height and aligned with the right edge');
                assert(await page.locator('[data-notifications-bell]').evaluate(e => { const badge = e.querySelector('.notificationBadge'); if (!badge) return false; const b = badge.getBoundingClientRect(), p = e.getBoundingClientRect(); return b.top >= p.top && b.right <= p.right + 1; }), 'Badge must stay inside header button bounds');
                const first = inbox.locator('section').first();
                await first.getByRole('button', { name: 'Keep', exact: true }).click();
                await page.waitForFunction(() => document.querySelector('#account-hub-inbox .notificationCommand[data-action=keep]')?.getAttribute('aria-pressed') === 'true');
                assert(await inbox.isVisible(), 'Inline consent must not dismiss the inbox or open a modal');
                assert(await first.locator('time').getAttribute('datetime'), 'Notification must expose an exact timestamp');
                assert.match(await first.locator('time').textContent(), /hours? ago/);
                assert.equal(await first.locator('.notificationMain').getAttribute('href'), '#/home');
                await first.locator('.notificationOptions > summary').click();
                await first.getByRole('menuitem', { name: 'Hide notification', exact: true }).waitFor();
                await inbox.getByRole('heading', { name: 'Notifications', exact: true }).click();
                assert.equal(await first.locator('.notificationOptions').evaluate(e => e.open), false);
                if (!await first.locator('.notificationDetails').evaluate(e => e.open)) await first.locator('.notificationDetails > summary').click();
                assert(await first.locator('.notificationDetails').evaluate(e => e.open), 'Notification details did not expand at ' + width);
                const actionSizes = await first.evaluate(e => ['compact','normal'].map(size => { const action = e.querySelector('[data-size=' + size + ']'); return { height: action.getBoundingClientRect().height, font: getComputedStyle(action).fontSize }; }));
                assert(actionSizes[0].height < actionSizes[1].height, 'Producer action sizes must differ: ' + JSON.stringify(actionSizes));
                await inbox.getByRole('button', { name: 'Older notifications', exact: true }).scrollIntoViewIfNeeded();
                await page.waitForFunction(() => document.querySelectorAll('#account-hub-inbox section').length === 40);
                assert.equal(await inbox.getByRole('heading', { name: 'Compact notification 0', exact: true }).count(), 1, 'Scrolling must retain earlier notifications without duplication');
                for (const group of ['Important', 'Other', 'Older']) assert.equal(await inbox.getByRole('heading', { name: group, exact: true }).count(), 1);
                if (process.env.ADMIN_SCREENSHOTS) {
                    await inbox.evaluate(e => { e.scrollTop = 0; });
                    await inbox.locator('section').first().locator('.notificationOptions > summary').click();
                    await inbox.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/notifications-menu-' + width + '.png' });
                }
                await page.getByRole('button', { name: 'Close notifications', exact: true }).click();
            }
            await page.unroute(inboxPattern);
            await page.unroute(actionPattern); assert(inlineActions >= 2);
        }
        // Installed injection + bounded DOM/API contracts; no deletion or real media access.
        const deletionItem = '11111111111111111111111111111111';
        let deletionPreviews = 0;
        await page.route('**/DeletionQueue/targets/items/*/capabilities', route => route.fulfill({ json: { canPropose: true } }));
        await page.route('**/DeletionQueue/targets/items/*/preview', route => {
            assert.equal(route.request().method(), 'POST');
            assert.deepEqual(route.request().postDataJSON(), {}, 'Removal preview must send valid JSON');
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
            menu.addEventListener('click', () => { menu.remove(); });
            document.body.append(menu);
        });
        await page.locator('.dq-context-action').waitFor({ state: 'attached' });
        await page.locator('.dq-context-action').dispatchEvent('click');
        await page.getByRole('heading', { name: 'Deletion UI fixture', exact: true }).waitFor();
        await page.locator('dialog.dq-panel').getByRole('button', { name: 'Close', exact: true }).click();
        phase = 'removal-context-native-dialog';
        await page.evaluate(() => {
            const menu = document.createElement('dialog'); menu.className = 'dialog actionSheet'; menu.id = 'dq-native-menu-fixture';
            menu.innerHTML = '<div class="actionSheetScroller"></div>'; document.body.append(menu);
            document.querySelector('#dq-installed-contract .card').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true }));
            menu.showModal();
        });
        await page.locator('#dq-native-menu-fixture').getByRole('button', { name: 'Request removal', exact: true }).click();
        await page.getByRole('heading', { name: 'Deletion UI fixture', exact: true }).waitFor();
        assert.equal(await page.locator('#dq-native-menu-fixture').evaluate(e => e.open), false, 'Native menu must dismiss before the request preview');
        await page.locator('dialog.dq-panel').getByRole('button', { name: 'Close', exact: true }).click();
        await page.locator('#dq-native-menu-fixture').evaluate(e => e.remove());
        assert.equal(deletionPreviews, 4, 'Detail, Enhanced request, removed menu and native dialog must each open a read-only preview');
        phase = 'enhanced-deletion-views';
        const proposalsPattern = '**/DeletionQueue/proposals?*';
        await page.route(proposalsPattern, route => {
            const history = new URL(route.request().url()).searchParams.get('view') === 'history';
            return route.fulfill({ json: [{ id: '22222222-2222-2222-2222-222222222222', title: history ? 'Past removal' : 'Pending removal', status: history ? 'Expired' : 'Blocked', reason: history ? 'RequestExpired' : 'ConsentRequired', revision: 1, canUndo: !history, ownAnswer: 0, ownSnapshot: 'fixture', stage: history ? 'History' : 'Consent', createdAt: new Date().toISOString() }] });
        });
        await page.evaluate(() => {
            const fixture = document.createElement('div'); fixture.id = 'dq-enhanced-page-contract';
            fixture.className = 'dialog'; fixture.style.cssText = 'position:fixed;inset:5rem 1rem 1rem;overflow:auto;z-index:1800;padding:1rem;';
            fixture.innerHTML = '<section class="je-requests-section"><h2>Requests</h2><div class="je-requests-tabs"><button>All</button></div><div class="je-requests-grid">Existing Seerr requests</div></section><div class="je-calendar-layout"><aside class="je-calendar-sidebar-content"></aside></div>';
            document.body.append(fixture);
        });
        const enhanced = page.locator('#dq-enhanced-page-contract');
        await enhanced.getByRole('button', { name: 'Deletion queue', exact: true }).click();
        await enhanced.locator('.dq-enhanced-view').getByRole('heading', { name: 'Pending removal', exact: true }).waitFor();
        assert.equal(await enhanced.locator('.dq-enhanced-view [data-answer=allow]').getAttribute('aria-pressed'), 'true');
        await enhanced.locator('.dq-enhanced-view').getByRole('tab', { name: 'History', exact: true }).click();
        await enhanced.locator('.dq-enhanced-view').getByRole('heading', { name: 'Past removal', exact: true }).waitFor();
        await enhanced.locator('.dq-calendar-removals').getByRole('heading', { name: 'Pending removal', exact: true }).waitFor();
        await enhanced.evaluate(e => e.remove()); await page.unroute(proposalsPattern);
        await page.evaluate(() => document.querySelector('#dq-installed-contract')?.remove());
        const adminPages = [];
        for (const [name, ready] of [['AccountHub', '.hubTabs'], ['privateSyncPlay', '#pspSave'], ['DiscordBridge', '.bridgeSettings'], ['DeletionQueueAdmin', '#dqPolicy'], ...(ownsNotifications ? [['Notifications', '[data-notification-settings] button']] : [])]) {
            phase = 'admin/' + name;
            let releaseNotifications;
            const settingsPattern = '**/Notifications/api/v1/settings/notifications';
            if (name === 'Notifications') {
                const gate = new Promise(resolve => { releaseNotifications = resolve; });
                await page.route(settingsPattern, async route => { await gate; await route.continue(); });
            }
            await page.goto(base.href.replace(/\/$/, '') + '/web/index.html#/configurationpage?name=' + name);
            await page.locator(ready).first().waitFor();
            if (name !== 'Notifications') await page.waitForLoadState('networkidle');
            if (name === 'AccountHub') await page.getByRole('tab', { name: 'Settings', exact: true }).click();
            if (name === 'Notifications') {
                const root = page.locator('#notificationsAdmin [data-notification-settings]');
                const catalog = root.locator('[data-notification-editor] > details');
                await catalog.locator(':scope > summary').click();
                const type = catalog.locator('fieldset').filter({ has: page.locator('legend', { hasText: 'Account notifications' }) }).first();
                try {
                    await type.waitFor();
                    assert.equal(await root.getAttribute('aria-busy'), 'true');
                    assert(await type.getByLabel('Layout', { exact: true }).isDisabled(), 'Notifications draft must stay disabled during initial loading');
                    await type.evaluate(e => { e.dataset.startupProbe = 'retained'; });
                } finally { releaseNotifications(); }
                await page.locator('#notificationsAdmin [data-notification-settings][aria-busy="false"] [data-notification-editor]:not([disabled])').waitFor();
                await page.unroute(settingsPattern);
                assert.equal(await type.getAttribute('data-startup-probe'), 'retained', 'Completing startup must not rebuild the early-opened catalog');
                await type.getByLabel('Layout', { exact: true }).selectOption('standard');
                await type.getByLabel('Summary length', { exact: true }).fill('180');
                const saved = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith('/settings/notifications/types/layout'));
                await type.getByRole('button', { name: 'Save layout', exact: true }).click();
                const saveResponse = await saved;
                assert.equal(saveResponse.request().postDataJSON().layout.density, 'standard');
                assert.equal(saveResponse.status(), 200, 'Layout save request failed');
                assert.equal((await saveResponse.json()).ok, true);
                await page.waitForFunction(() => [...document.querySelectorAll('#notificationsAdmin [data-notification-settings] [role=status]')].some(node => node.textContent === 'Saved / refreshed.'));
                assert.equal(await type.getByLabel('Layout', { exact: true }).inputValue(), 'standard');
                const persisted = await page.evaluate(async () => {
                    const result = await ApiClient.ajax({ url: ApiClient.getUrl('Notifications/api/v1/settings/notifications/types'), type: 'GET', dataType: 'json' });
                    return result.data.catalogs.find(catalog => catalog.source === 'account-hub').types.find(type => type.type === '*').layout;
                });
                assert.equal(persisted.density, 'standard'); assert.equal(persisted.summaryLength, 180);
                await catalog.locator(':scope > summary').click();
            }
            if (name === 'privateSyncPlay') {
                await page.locator('#pspForm[aria-busy="false"] #pspSave:enabled').waitFor();
                // Hold an actual refresh at dependency loading: network-idle or
                // the previous Save button alone does not establish readiness.
                let release;
                const gate = new Promise(resolve => { release = resolve; });
                const pending = page.waitForRequest(request => request.url().endsWith('/PrivateSyncPlay/admin/dependencies'));
                const pattern = '**/PrivateSyncPlay/admin/dependencies';
                await page.route(pattern, async route => { await gate; await route.continue(); });
                await page.locator('#privateSyncPlaySettings').dispatchEvent('viewshow');
                await pending;
                try {
                    assert.equal(await page.locator('#pspForm').getAttribute('aria-busy'), 'true');
                    assert(await page.locator('.privateSyncPlaySettingsEditor').evaluate(e => e.disabled), 'SyncPlay draft must remain disabled during refresh');
                } finally { release(); }
                await page.locator('#pspForm[aria-busy="false"] #pspSave:enabled').waitFor();
                await page.unroute(pattern);
            }
            const help = page.locator('.suiteAdmin:visible .suiteHelpButton:visible').first(); await help.waitFor();
            for (const width of [320, 1280]) {
                await page.setViewportSize({ width, height: 900 });
                if (name === 'privateSyncPlay') await page.locator('#pspForm[aria-busy="false"] #pspSave:enabled').waitFor();
                await page.waitForFunction(() => {
                    const button = [...document.querySelectorAll('.suiteAdmin .suiteHelpButton')].find(e => e.getClientRects().length);
                    return button && !button.matches(':disabled');
                });
                if (name === 'DiscordBridge') assert(await page.locator('.discordBridgePage:visible .checkboxContainer').evaluateAll(items => items.every(item => {
                    const label = item.querySelector('label'); return !label || label.getBoundingClientRect().bottom <= item.getBoundingClientRect().bottom + 2;
                })), name + '/' + width + ': checkbox text escapes its row');
                await help.blur(); await help.focus();
                assert.equal(await help.getAttribute('aria-expanded'), 'true', name + '/' + width + ': keyboard help did not open ' + JSON.stringify(await help.evaluate(e => ({ disabled: e.matches(':disabled'), focused: e === document.activeElement, parent: e.parentElement.outerHTML.slice(0, 900), active: document.activeElement?.outerHTML.slice(0, 300) }))));
                assert(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2), name + '/' + width + ': admin page overflows');
                await help.press('Escape'); assert.equal(await help.getAttribute('aria-expanded'), 'false');
                await help.blur(); await help.hover(); assert.equal(await help.getAttribute('aria-expanded'), 'true');
                await page.mouse.move(0, 0);
                if (process.env.ADMIN_SCREENSHOTS) await page.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/' + name + '-' + width + '.png', fullPage: true });
                adminPages.push({ name, width, help: true, noOverflow: true });
            }
        }
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
        console.log(JSON.stringify({ checks: results, customThemeOverride: true, keyboardTabNavigation: true, syncplaySettings: true, adminPages, activity }));
    } catch (error) { throw new Error(phase + ': ' + error.message); }
    finally { await browser.close(); relay.closeAllConnections(); await new Promise(resolve => relay.close(resolve)); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
