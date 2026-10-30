// Run only against the explicitly retained disposable integration fixture. Never load .env.
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const { openBrowserRelay } = require('./browser-relay.cjs');
(async () => {
    assert.equal(process.env.DISPOSABLE_THEME_TEST, 'true');
    let base = new URL(process.env.THEME_TEST_BASE_URL);
    assert(['127.0.0.1', 'localhost', 'host.docker.internal'].includes(base.hostname));
    assert(!base.username && !base.password && !base.search && !base.hash);
    // A container-local loopback relay gives Web Crypto its normal localhost
    // secure context without disabling browser security or changing plugin code.
    const upstream = base;
    const relay = await openBrowserRelay(upstream);
    base = new URL(relay.baseUrl);
    const browser = await chromium.launch({ headless: true });
    let phase = 'login';
    try {
        const page = await browser.newPage();
        const pageErrors = []; page.on('pageerror', error => pageErrors.push(error.message));
        await page.goto(base.href.replace(/\/$/, '') + '/web/index.html#/configurationpage?name=AccountHub');
        await page.locator('#txtManualName').fill('suite-admin');
        await page.locator('#txtManualPassword').fill('Disposable-Only-Suite-9714!');
        await page.locator('#txtManualPassword').press('Enter');
        await page.getByRole('tab', { name: 'People', exact: true }).waitFor();
        const results = [], themeSurfaces = {}, notificationPalettes = [], notificationSwipes = [], discordPermissions = []; let notificationCategories = [];
        async function applyTheme(theme) {
            await page.evaluate(async theme => {
                // Jellyfin 12's native palette is selected independently of legacy theme.css.
                document.documentElement.dataset.theme = theme;
                for (const link of document.querySelectorAll('link[href*="/themes/"]')) link.remove();
                const link = document.createElement('link'); link.rel = 'stylesheet';
                link.href = new URL('themes/' + theme + '/theme.css', document.baseURI).href;
                await new Promise((resolve, reject) => { link.onload = resolve; link.onerror = reject; document.head.append(link); });
            }, theme);
        }
        for (const theme of ['dark', 'light']) {
            await applyTheme(theme);
            themeSurfaces[theme] = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--jf-palette-background-default').trim());
            assert(themeSurfaces[theme], 'Native Jellyfin palette must be present');
            if (theme === 'light') assert.notEqual(themeSurfaces.light, themeSurfaces.dark, 'Light checks must use the light palette, not dark native CSS variables');
            for (const width of [320, 390, 1280]) {
                await page.setViewportSize({ width, height: 900 });
                for (const name of ['People', 'Groups', 'Invites', 'Notifications', 'Settings']) {
                    phase = `workspace/${theme}/${width}/${name}`;
                    const tab = page.getByRole('tab', { name, exact: true });
                    await tab.click();
                    const panel = page.getByRole('tabpanel', { name, exact: true });
                    await page.waitForFunction(() => document.querySelector('.hubMessage')?.textContent !== 'Loading…');
                    assert.equal(await page.locator('.hubMessage').textContent(), '', `${theme}/${width}/${name}: workspace failed to load`);
                    await panel.locator(':scope > *').first().waitFor();
                    await page.waitForLoadState('networkidle');
                    if (name === 'Invites') {
                        await panel.locator('summary').filter({ hasText: 'Invitation defaults' }).click();
                        await panel.getByLabel('Default template', { exact: true }).waitFor({ state: 'visible' });
                        const help = panel.getByRole('button', { name: 'About Default template', exact: true }).locator('..');
                        await help.waitFor({ state: 'visible' });
                        await help.locator('button').focus(); await help.locator('button').press('Enter');
                        assert.match(await help.locator('.suiteHelpText').textContent(), /Administrator privileges, credentials and 2FA are never copied/);
                        await help.locator('button').press('Escape');
                        assert(await help.locator('.suiteHelpText').isHidden());
                    }
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
                    // Exercise the permission editors and expanded advanced settings, not just tab landing pages.
                    if (name === 'People' || name === 'Groups' || name === 'Settings') {
                        let detail = panel;
                        if (name === 'People') {
                            await panel.locator('button:not([disabled])').filter({ hasText: /^Edit access$/ }).first().click();
                            detail = page.locator('.accountHubDialog'); await detail.waitFor();
                            await detail.getByRole('button', { name: 'Preview', exact: true }).click();
                            await page.waitForFunction(() => ![...document.querySelectorAll('.accountHubDialog button')].find(button => button.textContent === 'Apply reviewed changes')?.disabled);
                            assert.doesNotMatch(await detail.locator('[role=status]').textContent(), /"before"/, 'Permission preview must be readable, not raw JSON');
                            const inherited = await detail.evaluate(dialog => ({ dialog: getComputedStyle(dialog).color, body: getComputedStyle(document.body).color, font: getComputedStyle(dialog).fontFamily, bodyFont: getComputedStyle(document.body).fontFamily }));
                            assert.equal(inherited.dialog, inherited.body, 'Permission dialogs must inherit theme foreground'); assert.equal(inherited.font, inherited.bodyFont);
                            await detail.getByRole('button', { name: 'Review rollback history', exact: true }).click();
                            await detail.getByRole('heading', { name: /^Rollback history for / }).waitFor();
                        } else if (name === 'Groups') {
                            await panel.getByRole('button', { name: 'Create group', exact: true }).click();
                            await panel.getByLabel('Group name', { exact: true }).waitFor();
                            assert.equal(await panel.locator('fieldset select').count(), 10);
                        } else {
                            await panel.getByText('Advanced settings', { exact: true }).click();
                            await panel.getByLabel('Extra verification level', { exact: true }).waitFor({ state: 'visible' });
                            await panel.getByText('Security webhook', { exact: true }).click();
                            await panel.getByLabel('Replace webhook secret', { exact: true }).waitFor({ state: 'visible' });
                        }
                        const expanded = await detail.evaluate(root => ({ overflow: root.scrollWidth > root.clientWidth + 2, unlabeled: [...root.querySelectorAll('input,select')].filter(input => !input.labels?.length).length }));
                        assert.equal(expanded.overflow, false, `${theme}/${width}/${name}: expanded editor overflows`); assert.equal(expanded.unlabeled, 0);
                        if (process.env.ADMIN_SCREENSHOTS) { await detail.evaluate(root => { root.scrollTop = 0; }); await detail.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/accounts-' + name.toLowerCase() + '-' + theme + '-' + width + '.png' }); }
                        if (name === 'People') await detail.getByRole('button', { name: 'Cancel', exact: true }).click();
                    }
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
            const muteInApp = page.getByRole('switch', { name: 'non-critical in-app notifications', exact: true });
            await muteInApp.click();
            await page.waitForFunction(() => document.querySelector('[aria-label="non-critical in-app notifications"]')?.getAttribute('aria-checked') === 'false');
            await page.getByText('Muted sources and types', { exact: true }).click();
            await page.getByRole('button', { name: 'Unmute * · * · in-app', exact: true }).click();
            await page.waitForFunction(() => document.querySelector('[aria-label="non-critical in-app notifications"]')?.getAttribute('aria-checked') === 'true');
            phase = 'personal-notification-categories';
            notificationCategories = await require('./notification-category-acceptance.cjs')(page, applyTheme, process.env.ADMIN_SCREENSHOTS);
            await page.keyboard.press('Escape');
            await page.locator('#account-hub-inbox').waitFor({ state: 'hidden' });
            const inboxPattern = '**/Notifications/api/v1/notifications?*';
            const actionPattern = '**/Notifications/api/v1/notifications/*/actions';
            const { notificationFixtureId } = require('./notification-theme-fixture.cjs');
            let selectedAnswer = 'allow', inlineActions = 0, consentSwipeFixture = false, fixtureEpoch = 0;
            const notificationFixtureTime=Date.now();
            const consentWrites = [];
            const swipeWrites = [];
            await page.route(/\/Notifications\/api\/v1\/notifications\/fixture-\d+-\d+\/(read|dismiss)$/, route => {
                assert.equal(route.request().method(), 'POST'); swipeWrites.push(new URL(route.request().url()).pathname.split('/').at(-1));
                if (consentSwipeFixture) consentWrites.push('dismiss');
                return route.fulfill({ json: { ok: true, data: { status: 'updated' } } });
            });
            await page.route(actionPattern, async route => {
                const command = route.request().postDataJSON(); assert(command.commandId && command.snapshot === 'fixture-snapshot');
                if (consentSwipeFixture) { assert.equal(command.id, 'allow'); assert.equal(command.revision, 1); consentWrites.push('allow'); }
                selectedAnswer = command.id; inlineActions++; await route.fulfill({ json: { ok: true, data: { status: 'updated' } } });
            });
            await page.route(inboxPattern, route => {
                const skip = Number(new URL(route.request().url()).searchParams.get('skip') || 0);
                return route.fulfill({ json: { ok: true, data: { hasMore: skip === 0, notifications: Array.from({ length: 20 }, (_, i) => ({ id: notificationFixtureId(fixtureEpoch, skip + i), read: false, event: {
                     title: 'Compact notification ' + (skip + i), body: 'A short summary of the update.', details: 'Expanded details stay inside the notification menu.',
                     ...(consentSwipeFixture ? { source: 'deletion-queue', type: 'proposal.updated' } : {}),
                    occurredAt: new Date(notificationFixtureTime - (i === 19 ? 8 * 86400000 : 2 * 3600000) - (skip+i)*1000).toISOString(), severity: i % 10 === 0 ? 'warning' : 'info',
                    actions: [{ label: 'Small action', link: '#/home', size: 'compact' }, { label: 'Normal action', link: '#/home', size: 'normal' }]
                    ,commands: ['allow','keep','undo'].map(id => ({ id, label: id[0].toUpperCase() + id.slice(1), revision: 1, snapshot: 'fixture-snapshot', enabled: true, selected: id === selectedAnswer }))
                } })) } } });
            });
            for (const theme of ['dark', 'light']) for (const width of [320, 1280]) {
                phase = 'notification-scroll/' + theme + '/' + width;
                // Closing a panel does not cancel its final pagination/count
                // request. Drain that case before changing mock response state.
                await page.waitForLoadState('networkidle');
                fixtureEpoch++;
                selectedAnswer = 'allow';
                await applyTheme(theme);
                await page.setViewportSize({ width, height: 900 });
                await page.locator('[data-notifications-bell]').click();
                const firstId = notificationFixtureId(fixtureEpoch, 0);
                const first = page.locator('#account-hub-inbox section[data-notification-id="' + firstId + '"]');
                phase = 'notification-scroll/' + theme + '/' + width + '/initial-selection';
                await first.waitFor();
                await page.waitForFunction(id => document.querySelector('#account-hub-inbox section[data-notification-id="' + id + '"] .notificationCommand[data-action=allow]')?.getAttribute('aria-pressed') === 'true', firstId);
                const inbox = page.locator('#account-hub-inbox');
                assert(await inbox.evaluate(e => { const r = e.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth + 1 && r.bottom <= innerHeight; }), 'Notification menu must fit the viewport');
                if (width > 600) assert(await inbox.evaluate(e => { const r = e.getBoundingClientRect(); return r.height <= innerHeight / 2 + 1 && Math.abs(innerWidth - r.right - 12) <= 2; }), 'Desktop inbox must be half-height and aligned with the right edge');
                assert(await page.locator('[data-notifications-bell]').evaluate(e => { const badge = e.querySelector('.notificationBadge'); if (!badge) return false; const b = badge.getBoundingClientRect(), p = e.getBoundingClientRect(); return b.top >= p.top && b.right <= p.right + 1; }), 'Badge must stay inside header button bounds');
                const commandsBeforeSwipe = inlineActions;
                for (const [dx, action] of [[85, 'read'], [-85, 'dismiss']]) {
                    const before = swipeWrites.length;
                    await first.dispatchEvent('pointerdown', { pointerType: 'touch', pointerId: 1, isPrimary: true, clientX: 150, clientY: 100 });
                    await first.dispatchEvent('pointermove', { pointerType: 'touch', pointerId: 1, isPrimary: true, clientX: 150 + dx, clientY: 103 });
                    await first.dispatchEvent('pointerup', { pointerType: 'touch', pointerId: 1, isPrimary: true, clientX: 150 + dx, clientY: 103 });
                    const actions = first.locator('.notificationSwipeActions'); await actions.waitFor({ state: 'visible' });
                    assert.equal(swipeWrites.length, before, 'Swipe requires confirmation');
                    await actions.locator('[data-swipe-action="' + action + '"]').click();
                    phase = 'notification-scroll/' + theme + '/' + width + '/swipe-' + action;
                    await page.waitForFunction(() => !document.querySelector('#account-hub-inbox section .notificationSwipeActions:not([hidden])'));
                    assert.equal(swipeWrites.at(-1), action); assert.equal(swipeWrites.length, before + 1);
                }
                assert.equal(inlineActions, commandsBeforeSwipe, 'Swipe must not execute removal commands');
                // The preceding action paints rows before its unread-count request
                // finishes. Let that poll settle before requesting a new fixture.
                await page.waitForLoadState('networkidle');
                consentSwipeFixture = true; selectedAnswer = ''; const beforeConsent = consentWrites.length;
                // Reopen through the normal UI to load the next fixture, rather
                // than depending on optional producer invalidation listeners.
                await page.locator('[data-notifications-bell]').click();
                await page.locator('[data-notifications-bell]').click();
                try { await first.locator('.notificationSwipeHint').waitFor(); }
                catch (error) { throw Error(error.message + ' · Consent fixture state: ' + JSON.stringify(await first.evaluate(row => ({ title: row.title, commands: [...row.querySelectorAll('.notificationCommand')].map(button => ({ id: button.dataset.action, selected: button.getAttribute('aria-pressed'), disabled: button.disabled })), hints: row.closest('#account-hub-inbox').querySelectorAll('.notificationSwipeHint').length }))) + ' · Page errors: ' + JSON.stringify(pageErrors)); }
                const pointerType = width > 600 ? 'mouse' : 'touch';
                const swipeSkin = await page.addStyleTag({ content: '.accountHubInbox { --theme-text-color: rgb(28,42,57); --theme-background-color: rgb(233,241,252); --theme-primary-color: rgb(44,105,70); } .accountHubInbox .notificationSwipePreview { border-radius: 0; }' });
                await first.dispatchEvent('pointerdown', { pointerType, pointerId: 1, isPrimary: true, button: 0, clientX: 100, clientY: 100 });
                await first.dispatchEvent('pointermove', { pointerType, pointerId: 1, isPrimary: true, button: 0, clientX: 200, clientY: 102 });
                await first.locator('.notificationSwipePreview').waitFor({ state: 'visible' });
                const swipePalette = await first.evaluate(row => {
                    const panel = getComputedStyle(row.closest('.accountHubInbox')), preview = getComputedStyle(row.querySelector('.notificationSwipePreview')), tray = getComputedStyle(row.querySelector('.notificationSwipeActions'));
                    return { color: preview.color, accent: preview.borderTopColor, radius: preview.borderTopLeftRadius, font: preview.fontFamily, panelFont: panel.fontFamily, trayColor: tray.color };
                });
                assert.equal(swipePalette.color, 'rgb(28, 42, 57)'); assert.equal(swipePalette.trayColor, swipePalette.color);
                assert.equal(swipePalette.accent, 'rgb(44, 105, 70)'); assert.equal(swipePalette.radius, '0px');
                assert.equal(swipePalette.font, swipePalette.panelFont, 'Swipe UI must inherit skin typography');
                if (process.env.ADMIN_SCREENSHOTS) await inbox.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/notification-consent-skin-' + theme + '-' + width + '.png' });
                await first.dispatchEvent('pointerup', { pointerType, pointerId: 1, isPrimary: true, button: 0, clientX: 200, clientY: 102 });
                await first.locator('.notificationSwipeHint').waitFor({ state: 'detached' });
                assert.deepEqual(consentWrites.slice(beforeConsent), ['allow', 'dismiss'], 'Consent must succeed before hiding');
                await swipeSkin.evaluate(style => style.remove());
                consentSwipeFixture = false;
                notificationSwipes.push({ theme, width, rightRead: true, leftHide: true, confirmRequired: true, consentThenDismiss: true, pointerType, skinOverride: true });
                await page.evaluate(() => {
                    window.__suiteNotificationActionRendered = false;
                    // The command refreshes its own projection, then broadcasts a
                    // removal invalidation. Wait for the resulting DOM replacement,
                    // not just the first selected-answer paint, before opening a menu.
                    window.addEventListener('suite-removal-changed', () => {
                        const panel = document.querySelector('#account-hub-inbox'), previous = panel.querySelector('section');
                        const observer = new MutationObserver(() => {
                            if (panel.querySelector('section') === previous) return;
                            observer.disconnect(); window.__suiteNotificationActionRendered = true;
                        });
                        observer.observe(panel, { childList: true });
                    }, { once: true });
                });
                phase = 'notification-scroll/' + theme + '/' + width + '/keep-selection';
                await first.getByRole('button', { name: 'Keep', exact: true }).click();
                await page.waitForFunction(id => document.querySelector('#account-hub-inbox section[data-notification-id="' + id + '"] .notificationCommand[data-action=keep]')?.getAttribute('aria-pressed') === 'true', firstId);
                phase = 'notification-scroll/' + theme + '/' + width + '/keep-invalidation';
                await page.waitForFunction(() => window.__suiteNotificationActionRendered === true);
                assert(await inbox.isVisible(), 'Inline consent must not dismiss the inbox or open a modal');
                assert(await first.locator('time').getAttribute('datetime'), 'Notification must expose an exact timestamp');
                assert.match(await first.locator('time').textContent(), /hours? ago/);
                assert.equal(await first.locator('.notificationMain').getAttribute('href'), '#/home');
                await first.locator('.notificationOptions > summary').click();
                await first.getByRole('menuitem', { name: 'Hide notification', exact: true }).waitFor();
                const palette = await inbox.evaluate(panel => {
                    const probe = document.createElement('span'); probe.style.color = 'var(--jf-palette-text-primary)'; probe.style.backgroundColor = 'var(--jf-palette-background-paper)'; panel.append(probe);
                    const expected = getComputedStyle(probe), actual = getComputedStyle(panel), menu = getComputedStyle(panel.querySelector('.notificationOptionsMenu'));
                    const colors = { expectedColor: expected.color, expectedBackground: expected.backgroundColor, color: actual.color, background: actual.backgroundColor, menuColor: menu.color, menuBackground: menu.backgroundColor };
                    probe.remove(); return colors;
                });
                assert.equal(palette.color, palette.expectedColor, theme + ': notification foreground must use the native palette');
                assert.equal(palette.background, palette.expectedBackground, theme + ': notification surface must use the native palette');
                assert.equal(palette.menuColor, palette.color); assert.equal(palette.menuBackground, palette.background);
                await inbox.getByRole('heading', { name: 'Notifications', exact: true }).click();
                assert.equal(await first.locator('.notificationOptions').evaluate(e => e.open), false);
                if (!await first.locator('.notificationDetails').evaluate(e => e.open)) await first.locator('.notificationDetails > summary').click();
                assert(await first.locator('.notificationDetails').evaluate(e => e.open), 'Notification details did not expand at ' + width);
                const actionSizes = await first.evaluate(e => ['compact','normal'].map(size => { const action = e.querySelector('[data-size=' + size + ']'); return { height: action.getBoundingClientRect().height, font: getComputedStyle(action).fontSize }; }));
                assert(actionSizes[0].height < actionSizes[1].height, 'Producer action sizes must differ: ' + JSON.stringify(actionSizes));
                await inbox.getByRole('button', { name: 'Older notifications', exact: true }).scrollIntoViewIfNeeded();
                phase = 'notification-scroll/' + theme + '/' + width + '/pagination';
                await page.waitForFunction(() => document.querySelectorAll('#account-hub-inbox section').length === 40);
                assert.equal(await inbox.getByRole('heading', { name: 'Compact notification 0', exact: true }).count(), 1, 'Scrolling must retain earlier notifications without duplication');
                for (const group of ['Important', 'Other', 'Older']) assert.equal(await inbox.getByRole('heading', { name: group, exact: true }).count(), 1);
                if (process.env.ADMIN_SCREENSHOTS) {
                    await inbox.evaluate(e => { e.scrollTop = 0; });
                    await inbox.locator('section').first().locator('.notificationOptions > summary').click();
                    await inbox.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/notifications-menu-' + theme + '-' + width + '.png' });
                }
                const skin = await page.addStyleTag({ content: '.accountHubInbox { --theme-text-color: rgb(28,42,57); --theme-background-color: rgb(233,241,252); }' });
                assert.deepEqual(await inbox.evaluate(panel => ({ color: getComputedStyle(panel).color, background: getComputedStyle(panel).backgroundColor })), { color: 'rgb(28, 42, 57)', background: 'rgb(233, 241, 252)' }, 'A custom skin must retain control of notification colors');
                await skin.evaluate(style => style.remove());
                notificationPalettes.push({ theme, width, nativePalette: true, skinOverride: true });
                await page.keyboard.press('Escape');
                if (await inbox.isVisible()) await page.keyboard.press('Escape'); // Close an open options menu first.
                await inbox.waitFor({ state: 'hidden' });
            }
            await page.unroute(inboxPattern);
            await page.unroute(actionPattern); assert(inlineActions >= 2);
        }
        // Installed injection + bounded DOM/API contracts; no deletion or real media access.
        const deletionItem = '11111111111111111111111111111111';
        let deletionPreviews = 0;
        let queuedMenu = false;
        await page.route('**/DeletionQueue/targets/items/*/removal', route => route.fulfill({ json: { canPropose: true, proposals: queuedMenu ? [{
            id: '22222222-2222-2222-2222-222222222222', title: 'Queued series', status: 'Waiting', statusLabel: 'Scheduled',
            revision: 3, ownSnapshot: 'fixture', ownAnswer: 1, canUndo: true,
            timeline: [{ key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-20T12:00:00Z' }, { key: 'delay', label: 'Safety delay', state: 'current', at: '2026-10-27T12:00:00Z' }]
        }] : [] } }));
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
        assert.equal(await page.locator('#dq-installed-contract .card .dq-media-action').count(), 0, 'Thumbnails must not contain removal actions');
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
        assert.equal(await page.locator('.dq-context-section .dq-timeline').count(), 0, 'Unqueued items must not show a timeline');
        assert.equal(await page.locator('.dq-context-action .material-icons').textContent(), 'delete_outline');
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
        queuedMenu = true;
        await page.evaluate(() => {
            document.querySelector('#dq-installed-contract .card').dispatchEvent(new MouseEvent('contextmenu', { bubbles: true }));
            const menu = document.createElement('dialog'); menu.className = 'dialog actionSheet'; menu.id = 'dq-queued-menu-fixture';
            menu.innerHTML = '<div class="actionSheetScroller"><button class="listItem listItem-button actionSheetMenuItem">Play</button></div>'; document.body.append(menu); menu.showModal();
        });
        const queuedContext = page.locator('#dq-queued-menu-fixture');
        await queuedContext.getByRole('button', { name: 'Undo removal request', exact: true }).waitFor();
        assert(await queuedContext.evaluate(e => getComputedStyle(e).color === getComputedStyle(document.body).color), 'Native context section must inherit theme foreground');
        assert.equal(await queuedContext.locator('[aria-current=step]').getAttribute('data-checkpoint'), 'delay');
        assert.equal(await queuedContext.getByRole('button', { name: 'Request removal', exact: true }).count(), 0);
        assert.equal(await queuedContext.getByRole('button', { name: 'Keep item (reject removal)', exact: true }).getAttribute('aria-pressed'), 'true');
        assert.equal(await queuedContext.locator('.dq-context-action .listItemIcon').count(), 4);
        if (process.env.ADMIN_SCREENSHOTS) await queuedContext.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/deletion-queue-context-menu.png' });
        await queuedContext.evaluate(e => { e.close(); e.remove(); });
        phase = 'enhanced-deletion-views';
        const proposalsPattern = '**/DeletionQueue/proposals?*';
        const approvalPattern='**/DeletionQueue/admin/approvals?*',approvalId='77777777-7777-7777-7777-777777777777';let needsApproval=true;
        await page.route(approvalPattern,route=>route.fulfill({json:needsApproval?[{id:approvalId,title:'Approval review fixture',status:'Blocked',reason:'AdministratorApprovalRequired',revision:4,canApprove:true,canConsent:false,canUndo:false,administratorApprovalSnapshot:'browser-approval-snapshot'}]:[]}));
        await page.route('**/DeletionQueue/admin/proposals/'+approvalId+'/approval',route=>{const body=route.request().postDataJSON();assert.equal(body.revision,4);assert.equal(body.snapshot,'browser-approval-snapshot');assert.equal(body.approved,true);needsApproval=false;return route.fulfill({json:{status:'updated'}});});
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
        const approvalView=enhanced.locator('.dq-enhanced-view');
        await approvalView.getByRole('heading',{name:'Approval review fixture',exact:true}).waitFor();
        assert.equal(await approvalView.getByRole('tab',{name:'Needs approval',exact:true}).getAttribute('aria-selected'),'true');
        assert.equal(await approvalView.locator('[data-answer]').count(),0,'Administrator review must not impersonate viewer consent');
        await approvalView.getByRole('button',{name:'Deny request',exact:true}).waitFor();
        await approvalView.getByRole('button',{name:'Approve deletion',exact:true}).click();
        await approvalView.getByText('No requests need approval.',{exact:true}).waitFor();
        await approvalView.getByRole('tab',{name:'Active',exact:true}).click();
        await enhanced.locator('.dq-enhanced-view').getByRole('heading', { name: 'Pending removal', exact: true }).waitFor();
        assert.equal(await enhanced.locator('.dq-enhanced-view [data-answer=allow]').getAttribute('aria-pressed'), 'true');
        await enhanced.locator('.dq-enhanced-view').getByRole('tab', { name: 'History', exact: true }).click();
        await enhanced.locator('.dq-enhanced-view').getByRole('heading', { name: 'Past removal', exact: true }).waitFor();
        assert.equal(await enhanced.locator('.dq-calendar-removals').count(), 0);
        await enhanced.evaluate(e => e.remove());
        phase = 'queue-review-timeline';
        const selectedRequest = '55555555-5555-5555-5555-555555555555';
        const detailPattern = '**/DeletionQueue/proposals/' + selectedRequest + '**';
        await page.route(detailPattern, route => route.fulfill({ json: {
            id: selectedRequest, title: 'Selected removal request fixture', status: 'Waiting', reason: 'DelayRequired', statusLabel: 'Scheduled',
            statusHelp: 'Removal can happen no earlier than the displayed date, after fresh safety checks.', revision: 1, canUndo: true, ownAnswer: 0, ownSnapshot: 'fixture', stage: 'Queued',
            evaluatedAt: '2026-10-25T12:00:00Z', scheduledFor: '2026-10-27T12:00:00Z', activityWindowDays: 30, safetyDelayHours: 144, mediaKind: 'Series',
            timeline: [
                { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-20T12:00:00Z' },
                { key: 'checks', label: 'Checks & consent', state: 'complete', at: '2026-10-21T12:00:00Z' },
                { key: 'delay', label: 'Safety delay', state: 'current', at: '2026-10-27T12:00:00Z' },
                { key: 'removing', label: 'Removal started', state: 'pending' },
                { key: 'confirmed', label: 'Removal confirmed', state: 'pending' },
                { key: 'closed', label: 'Completed', state: 'pending' }
            ]
        } }));
        await page.evaluate(id => {
            const link = document.createElement('a'); link.id = 'dq-review-contract'; link.textContent = 'Review queue fixture'; link.href = '#/home?deletionQueueProposal=' + id;
            link.style.cssText = 'position:fixed;top:5rem;left:1rem;z-index:1800;'; document.body.append(link);
        }, selectedRequest);
        const beforeReview = page.url();
        await page.getByRole('link', { name: 'Review queue fixture', exact: true }).click();
        const queueDialog = page.locator('dialog.dq-panel');
        await queueDialog.getByRole('heading', { name: 'Deletion queue', exact: true }).waitFor();
        assert(await queueDialog.evaluate(e => getComputedStyle(e).color === getComputedStyle(document.body).color), 'HTML dialog must inherit native text color, not user-agent CanvasText');
        const skinColor = await page.addStyleTag({ content: '.dialog.dq-panel { color: rgb(181, 193, 211); }' });
        assert.equal(await queueDialog.evaluate(e => getComputedStyle(e).color), 'rgb(181, 193, 211)', 'A custom skin must retain foreground control');
        await skinColor.evaluate(e => e.remove());
        const selectedRow = queueDialog.locator('.dq-highlight');
        await selectedRow.getByRole('heading', { name: 'Selected removal request fixture', exact: true }).waitFor();
        assert.equal(page.url(), beforeReview, 'Review must open the queue, not navigate away');
        assert.equal(await selectedRow.getAttribute('data-request-id'), selectedRequest);
        assert.equal(await selectedRow.locator('.dq-timeline li').count(), 3);
        assert.equal(await selectedRow.locator('[aria-current=step]').getAttribute('data-checkpoint'), 'delay');
        assert.equal(await selectedRow.locator('.dq-timeline time').first().getAttribute('datetime'), '2026-10-20T12:00:00.000Z');
        assert.equal(await selectedRow.locator('.dq-media-marker .material-icons').count(), 0, 'Media position is a compact dot, not an icon');
        assert.equal(await selectedRow.locator('.dq-travelled').count(),1,'Completed track must be highlighted');
        assert(await selectedRow.evaluate(e => parseFloat(e.querySelector('[data-checkpoint=checks]').style.getPropertyValue('--dq-position')) < parseFloat(e.querySelector('.dq-media-marker').style.getPropertyValue('--dq-position'))), 'Media marker must reflect server time relative to dated nodes');
        for (const width of [320, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            const help = selectedRow.getByRole('button', { name: 'About removal status', exact: true });
            await help.hover(); await selectedRow.getByRole('tooltip').waitFor({ state: 'visible' });
            assert.match(await selectedRow.getByRole('tooltip').textContent(), /fresh safety checks/);
            await help.focus(); await help.press('Escape');
            await selectedRow.getByRole('tooltip').waitFor({ state: 'hidden' });
            assert(await queueDialog.evaluate(e => e.open), 'Escape should dismiss status help before the queue');
            const modalOverflow=await queueDialog.evaluate(e=>({overflow:e.scrollWidth>e.clientWidth+2,elements:[...e.querySelectorAll('*')].filter(n=>n.getBoundingClientRect().right>e.getBoundingClientRect().right+2&&!n.closest('.dq-timeline')).map(n=>({tag:n.tagName,classes:n.className,width:n.getBoundingClientRect().width})).slice(0,8)}));
            assert(!modalOverflow.overflow, 'Timeline must scroll within its row, not overflow the modal: '+JSON.stringify(modalOverflow.elements));
            assert(await selectedRow.locator('.dq-timeline').evaluate(e => {
                e.scrollLeft = e.scrollWidth;
                const reached = e.lastElementChild.getBoundingClientRect().right <= e.getBoundingClientRect().right + 1;
                e.scrollLeft = 0; return reached;
            }), 'The final timeline checkpoint must be reachable by horizontal scrolling');
            if (process.env.ADMIN_SCREENSHOTS) await queueDialog.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/deletion-queue-timeline-' + width + '.png' });
        }
        const box = await queueDialog.boundingBox();
        await page.mouse.click(box.x - 5, box.y - 5);
        await queueDialog.waitFor({ state: 'detached' });
        await page.unroute(detailPattern);
        phase='compact-branched-timeline-drag';
        await page.route(detailPattern,route=>route.fulfill({json:{id:selectedRequest,title:'Current media timeline fixture',status:'Blocked',reason:'ConsentRequired',revision:1,canConsent:false,canUndo:false,
            timeline:[{key:'requested',label:'Requested',state:'complete'},{key:'checks',label:'Checks',state:'current',paths:[{key:'activity',label:'All users inactive',state:'pending',durationDays:14},{key:'consent',label:'All active viewers consent',state:'current'},{key:'activity-timeout',label:'Activity wait expired',state:'pending',durationDays:45}]},{key:'approval',label:'Review',state:'pending',paths:[{key:'approved',label:'Approved',state:'pending'},{key:'approval-timeout',label:'Denied or timed out',state:'pending',durationDays:30}]},{key:'delay',label:'Safety delay',state:'pending',durationHours:48},{key:'removing',label:'Verify and remove',state:'pending'}]}}));
        await page.getByRole('link',{name:'Review queue fixture',exact:true}).click();const flow=queueDialog.locator('.dq-highlight .dq-flow-timeline');await flow.waitFor();
        for(const width of [320,1280]){
            await page.setViewportSize({width,height:900});await flow.scrollIntoViewIfNeeded();
            await page.waitForFunction(()=>!!document.querySelector('.dq-highlight .dq-flow-timeline')?.dataset.scale);
            const metric=await flow.evaluate(e=>{const mark=e.querySelector('[aria-current=step]').getBoundingClientRect(),box=e.getBoundingClientRect();return{scale:Number(e.dataset.scale),font:parseFloat(getComputedStyle(e).fontSize)*Number(e.dataset.scale),height:box.height,center:Math.abs(mark.left+mark.width/2-box.left-e.clientWidth/2),width:e.clientWidth};});
            assert(metric.scale>=.85&&metric.scale<=1&&metric.font>=10,'Timeline must preserve a readable minimum scale');assert(metric.height<400,'Branched timeline must stay compact');assert(metric.center<metric.width*.4,'Current media stage must start in view near the centre');
            const before=await flow.evaluate(e=>e.scrollLeft),rect=await flow.boundingBox(),dialogRect=await queueDialog.boundingBox();const x=rect.x+rect.width*.65,y=Math.min(dialogRect.y+dialogRect.height-30,Math.max(dialogRect.y+70,rect.y+rect.height/2));
            await page.mouse.move(x,y);await page.mouse.down();await page.mouse.move(x-90,y,{steps:5});await page.mouse.up();assert(await flow.evaluate(e=>e.scrollLeft)>before+20,'Dragging the timeline must scroll horizontally');
            await flow.press('Home');
            if(process.env.ADMIN_SCREENSHOTS)await queueDialog.screenshot({path:process.env.ADMIN_SCREENSHOTS+'/compact-deletion-flow-'+width+'.png'});
        }
        await queueDialog.press('Escape');await queueDialog.waitFor({state:'detached'});await page.unroute(detailPattern);
        for (const denied of [false, true]) {
            phase = denied ? 'approval-fork-history' : 'approval-fork-pending';
            await page.route(detailPattern, route => route.fulfill({ json: {
                id: selectedRequest, title: 'Administrator decision fixture', status: denied ? 'Expired' : 'Blocked', reason: denied ? 'AdministratorDenied' : 'AdministratorApprovalRequired',
                closedCheckpoint: denied ? 'approval' : null, closureExplanation: denied ? 'This request was denied and closed.' : null,
                timeline: [
                    { key: 'requested', label: 'Requested', state: 'complete', at: '2026-10-20T12:00:00Z' },
                    { key: 'checks', label: 'Inactive', state: 'complete', at: '2026-10-20T12:00:00Z' },
                    { key: 'approval', label: 'Administrator consent', state: denied ? 'cancelled' : 'current', decision: denied ? 'denied' : 'pending' },
                    { key: 'delay', label: 'Safety delay', state: denied ? 'stopped' : 'pending' },
                    { key: 'removing', label: 'Removal started', state: 'pending' },
                    { key: 'closed', label: denied ? 'Cancelled' : 'Completed', state: denied ? 'complete' : 'pending', at: denied ? '2026-10-22T12:00:00Z' : null }
                ]
            } }));
            await page.getByRole('link', { name: 'Review queue fixture', exact: true }).click();
            const forkRow = queueDialog.locator('.dq-highlight'); await forkRow.locator('.dq-approval-fork').waitFor();
            assert.equal(await forkRow.locator('li[data-checkpoint=removing]').count(), 0);
            if (!denied) assert.equal(await forkRow.locator('.dq-media-marker').getAttribute('data-checkpoint'), 'approval');
            else assert.match(await forkRow.locator('.dq-approval-fork .dq-branch-selected').textContent(), /Denied/);
            for (const width of [320, 1280]) {
                await page.setViewportSize({ width, height: 900 });
                await page.waitForFunction(() => {
                    const marker = document.querySelector('dialog.dq-panel .dq-highlight .dq-media-marker');
                    if (!marker) return false;
                    const box = marker.getBoundingClientRect(), viewport = marker.closest('.dq-timeline').getBoundingClientRect();
                    return box.left >= viewport.left - 1 && box.right <= viewport.right + 1;
                });
                assert(await queueDialog.evaluate(e => e.scrollWidth <= e.clientWidth + 2), phase + ': no horizontal dialog overflow');
                assert(await forkRow.locator('.dq-dated-nodes').evaluate(e => {
                    const boxes = [...e.children].map(child => child.getBoundingClientRect());
                    return boxes.every((a, i) => boxes.slice(i + 1).every(b => a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top));
                }), phase + ': checkpoint labels and branch paths must not overlap');
                if (process.env.ADMIN_SCREENSHOTS) await queueDialog.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/' + phase + '-' + width + '.png' });
            }
            await queueDialog.evaluate(e => e.close()); await page.unroute(detailPattern);
        }
        await page.locator('#dq-review-contract').evaluate(e => e.remove());
        await page.unroute(detailPattern); await page.unroute(proposalsPattern);
        phase = 'enhanced-latest-native-calendar';
        await require('./enhanced-calendar-acceptance.cjs')(page);
        await page.evaluate(() => document.querySelector('#dq-installed-contract')?.remove());
        const adminPages = [];
        for (const [name, ready] of [['AccountHub', '.hubTabs'], ['privateSyncPlay', '#pspSave'], ['DiscordBridge', '.bridgeSettings'], ['DeletionQueueAdmin', '#dqPolicy'], ...(process.env.THEME_TEST_AUTOMATIC === 'true' ? [['AutomaticDeletionRequests', '#arForm']] : []), ...(ownsNotifications ? [['Notifications', '[data-notification-settings] button']] : [])]) {
            phase = 'admin/' + name;
            const errorStart = pageErrors.length;
            let releaseNotifications;
            const settingsPattern = '**/Notifications/api/v1/settings/notifications';
            if (name === 'Notifications') {
                const gate = new Promise(resolve => { releaseNotifications = resolve; });
                await page.route(settingsPattern, async route => { await gate; await route.continue(); });
            }
            await page.goto(base.href.replace(/\/$/, '') + '/web/index.html#/configurationpage?name=' + name);
            await page.locator(ready).first().waitFor();
            if (name !== 'Notifications') await page.waitForLoadState('networkidle');
            await page.waitForFunction(() => [...document.querySelectorAll('.suiteAdmin:not(.hide) .suiteIntegrationBadge')].some(e => e.getClientRects().length && /v\d+\./.test(e.textContent)));
            for (const id of ['account-hub','notifications','discord','private-syncplay','deletion-queue','automatic']) if (id !== 'automatic' || process.env.THEME_TEST_AUTOMATIC === 'true') {
                const badge = page.locator('.suiteAdmin:visible .suiteIntegrationBadge[data-plugin="'+id+'"]');
                assert.match(await badge.textContent(), /v\d+\./, name + ': missing installed version for ' + id);
            }
            if (name === 'AccountHub') await page.getByRole('tab', { name: 'Settings', exact: true }).click();
            if (name === 'AutomaticDeletionRequests') {
                await page.locator('#arForm[aria-busy="false"]').waitFor();
                assert(await page.locator('#arForm').evaluate(e=>{const p=e.closest('.content-primary'),s=getComputedStyle(p);return Math.abs(e.getBoundingClientRect().width-(p.clientWidth-parseFloat(s.paddingLeft)-parseFloat(s.paddingRight)))<3;}),'Automation must use the available settings width');
                assert.equal(await page.locator('#arEnabled').isChecked(), false);
                await page.getByRole('button', { name: 'New card', exact: true }).click();
                await page.locator('#arNewTitle').fill('Browser rule');
                await page.locator('#arNewDescription').fill('Disposable canvas verification');
                await page.locator('#arNewCreate').click();
                await page.locator('.ar-block[data-kind=condition]').waitFor();
                const condition=page.locator('.ar-block[data-kind=condition]'),before=await condition.evaluate(e=>parseFloat(e.style.left));
                await condition.locator('header').scrollIntoViewIfNeeded();
                const header=await condition.locator('header').boundingBox();
                await page.mouse.move(header.x+40,header.y+12);await page.mouse.down();await page.mouse.move(header.x+90,header.y+42,{steps:4});await page.mouse.up();
                assert((await condition.evaluate(e=>parseFloat(e.style.left)))>before,'Node dragging must change persisted coordinates');
                await page.getByRole('button', { name: 'New card', exact: true }).click();
                await page.locator('#arNewType').selectOption('filter');await page.locator('#arNewTitle').fill('Browser library filter');await page.locator('#arNewCreate').click();
                await page.locator('#arRuleCards label').filter({hasText:'Browser rule'}).click();
                assert(await page.getByLabel('Browser rule',{exact:true}).isChecked(),'Filter output must attach the rule');
                const selector=page.getByLabel('Select card',{exact:true});
                const ruleId=await selector.locator('option').evaluateAll(options=>options.find(o=>o.textContent==='Rule · Browser rule').value);
                await selector.selectOption(ruleId);
                const source=page.locator('.ar-source').filter({hasText:'Browser library filter'});await source.waitFor();assert.equal(await source.locator('.ar-socket').count(),1,'Library output must be editable in rule mode');
                assert.equal(await page.locator('.ar-block[data-kind=start]').count(),0,'Typed circuits have no execution Start');
                assert(await page.locator('.ar-wires path[data-scope]').count()>0);
                await page.getByRole('button',{name:'Fit canvas',exact:true}).click();
                await source.getByRole('button',{name:'Disconnect',exact:true}).click();
                await source.getByRole('button',{name:'Connect',exact:true}).click();
                assert(await page.locator('.ar-wires path[data-scope]').count()>0);
                const skin=await page.addStyleTag({content:'#automaticDeletionAdmin {color:rgb(170,195,210);--theme-primary-color:rgb(180,90,170)} #automaticDeletionAdmin .ar-circuit.paperList,#automaticDeletionAdmin .ar-block.paperList {background-color:rgb(32,42,52)}'});
                assert.equal(await page.locator('.ar-circuit').evaluate(e=>getComputedStyle(e).color),'rgb(170, 195, 210)','Canvas must inherit skin text colour');
                assert.equal(await page.locator('.ar-circuit').evaluate(e=>getComputedStyle(e).backgroundColor),'rgb(32, 42, 52)','Native paperList surface must accept skin overrides');
                assert.equal(await page.locator('.ar-block[data-kind=condition] .ar-output').evaluate(e=>getComputedStyle(e).color),'rgb(180, 90, 170)','Sockets must follow the skin accent');
                await skin.evaluate(e=>e.remove());
            }
            if (name === 'DeletionQueueAdmin') {
                const policyWidth=await page.locator('#dqPolicy').evaluate(e=>{const parent=e.closest('.content-primary'),container=getComputedStyle(parent);return{width:e.getBoundingClientRect().width,available:parent.clientWidth-parseFloat(container.paddingLeft)-parseFloat(container.paddingRight)};});
                assert(Math.abs(policyWidth.width-policyWidth.available)<3,'Policy must use the available settings width: '+JSON.stringify(policyWidth));
                const policyGraph=page.locator('#dqPolicyTimeline .dq-flow-timeline');await policyGraph.waitFor();
                assert.equal(await policyGraph.locator('[data-checkpoint=activity]').count(),1);
                assert.equal(await policyGraph.locator('[data-checkpoint=consent]').count(),1);
                assert(await policyGraph.locator('path[data-from=requested][data-to=activity]').count());
                assert(await policyGraph.locator('path[data-from=requested][data-to=consent]').count());
                const approvalToggle=page.locator('#dqRequireAdminApproval'),required=await approvalToggle.isChecked();
                if(!required)await page.getByText('Require administrator approval',{exact:true}).click();
                const approvalDays=page.locator('#dqApprovalDays'),savedDays=await approvalDays.inputValue();
                await approvalDays.fill('45');
                await page.waitForFunction(()=>document.querySelector('#dqPolicyTimeline .dq-flow-edge-label[data-to="approval-timeout"]')?.textContent.includes('45-day decision limit'));
                assert(await policyGraph.locator('path[data-from="approval-timeout"][data-to=cancelled]').count());
                assert.equal(await policyGraph.locator('path[data-from="approval-timeout"][data-to=delay]').count(),0);
                assert.equal(await policyGraph.locator('[data-checkpoint=completed]').count(),1);
                assert.equal(await policyGraph.locator('[data-checkpoint=cancelled]').count(),1);
                await approvalDays.fill(savedDays);if(!required)await page.getByText('Require administrator approval',{exact:true}).click();
                const waitingToggle=page.locator('#dqActivityTimeoutEnabled'),waitingEnabled=await waitingToggle.isChecked(),waitingDays=page.locator('#dqActivityTimeoutDays'),savedWaitingDays=await waitingDays.inputValue();
                if(!waitingEnabled)await page.getByText('Limit activity/consent waiting',{exact:true}).click();
                await waitingDays.fill('45');await page.waitForFunction(()=>document.querySelector('#dqPolicyTimeline .dq-flow-edge-label[data-to="activity-timeout"]')?.textContent.includes('45-day waiting limit'));
                assert(await policyGraph.locator('path[data-from="activity-timeout"][data-to=cancelled]').count());
                await waitingDays.fill(savedWaitingDays);if(!waitingEnabled)await page.getByText('Limit activity/consent waiting',{exact:true}).click();
                const editor = page.locator('#dqPresentation');
                try { await editor.getByRole('button', { name: 'Preview portrait', exact: true }).waitFor(); }
                catch (error) { throw new Error(error.message + ' · Presentation status: ' + await editor.locator('[data-presentation-status]').textContent()); }
                const copy = page.getByRole('button', { name: 'Copy Jellyfin Enhanced connection', exact: true });
                try { await page.locator('#deletionQueueAdmin[aria-busy="false"] #dqEnhanced:enabled').waitFor(); }
                catch (error) { throw new Error('Monitoring settings were not ready: ' + await page.locator('#dqError').textContent() + ' · ' + error.message); }
                assert.equal(await page.locator('#dqEnhancedStatus').textContent(), ''); assert(await copy.isEnabled(), 'Enhanced copy control must remain enabled when its optional provider is absent'); await copy.click();
                assert.match(await page.locator('#dqEnhancedStatus').textContent(), /exactly one/);
                const connectionTest = page.waitForResponse(r => r.url().endsWith('/admin/seerr/test') && r.request().method() === 'POST');
                await page.locator('#dqKey').fill('disposable-draft-not-sent'); await page.getByRole('button', { name: 'Test connection', exact: true }).click();
                const tested = await connectionTest; assert.equal(tested.status(), 200); assert.deepEqual(tested.request().postDataJSON(), {});
                assert.match(await page.locator('#dqConnectionResult').textContent(), /Save a Seerr/); await page.locator('#dqKey').fill('');
                const studio = editor.locator('.dq-canvas-editor').first(), artboard = studio.locator('.dq-canvas-artboard');
                assert(await editor.locator('[data-presentation-editor] > form').evaluate(e=>{const p=e.parentElement,s=getComputedStyle(p);return Math.abs(e.getBoundingClientRect().width-(p.clientWidth-parseFloat(s.paddingLeft)-parseFloat(s.paddingRight)))<3;}),'Overlay Studio form must use the available width');
                assert(await studio.locator('.dq-canvas-layers').evaluate(e=>Math.abs(e.getBoundingClientRect().width-e.parentElement.getBoundingClientRect().width)<3),'Layers must span the editor, not leave a narrow empty left column');
                assert.equal(await studio.locator('.dq-canvas-align button .material-icons').count(),6);
                assert(await studio.locator('.dq-canvas-inspector').evaluate(e=>getComputedStyle(e).gridTemplateColumns.split(' ').length>=2),'Layer properties must use compact columns');
                await studio.getByRole('button', { name: 'Add text', exact: true }).click();
                const box = await studio.locator('.dq-canvas-selected').boundingBox(), board = await artboard.boundingBox();
                await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); await page.mouse.down(); await page.mouse.move(box.x + box.width / 2 + board.width * .05, box.y + box.height / 2 + board.height * .05, { steps: 3 }); await page.mouse.up();
                assert(Number(await studio.getByLabel('X (%)', { exact: true }).inputValue()) > 12, 'Canvas drag must update normalized layer geometry');
                await studio.getByRole('button', { name: 'Undo', exact: true }).click();
                assert.equal(Number(await studio.getByLabel('X (%)', { exact: true }).inputValue()), 10);
                await studio.getByRole('button', { name: 'Delete layer', exact: true }).click();
                for (const [layout, width, height] of [['portrait', 480, 720], ['landscape', 800, 450]]) {
                    const preview = page.waitForResponse(r => r.url().endsWith('/presentation/preview') && r.request().method() === 'POST');
                    await editor.getByRole('button', { name: 'Preview ' + layout, exact: true }).click();
                    const response = await preview; assert.equal(response.status(), 200); assert(response.headers()['content-type'].includes('image/png'), 'Expected native PNG preview, got ' + response.headers()['content-type']);
                    const image = editor.getByAltText(layout[0].toUpperCase() + layout.slice(1) + ' sample artwork preview');
                    await image.evaluate(img => img.decode());
                    assert.deepEqual(await image.evaluate(img => [img.naturalWidth, img.naturalHeight]), [width, height]);
                }
                for (const width of [320, 1280]) {
                    await page.setViewportSize({ width, height: 900 });
                    assert(await editor.evaluate(e => e.scrollWidth <= e.clientWidth + 2), 'Presentation editor overflows at ' + width);
                }
                const journal = page.locator('#dqJournal').locator('..'); await journal.locator(':scope > summary').click();
                await journal.locator('#dqJournal table').waitFor();
                for (const heading of ['Sequence', 'Time', 'Event', 'Actor']) await journal.getByRole('columnheader', { name: heading, exact: true }).waitFor();
                assert(await journal.locator('#dqJournal tbody tr').count() > 0, 'Journal must show recorded transactions');
                await journal.locator(':scope > summary').click();
                const taskLink = editor.locator('.suiteJobBadge').first(); assert.match(await taskLink.textContent(), /Daily/); await taskLink.click(); await page.waitForURL(/#\/dashboard\/tasks\//);
                await page.goto(base.href.replace(/\/$/, '') + '/web/index.html#/configurationpage?name=DeletionQueueAdmin'); await page.locator('#dqPresentation .dq-canvas-editor').first().waitFor(); await page.waitForLoadState('networkidle');
            }
            if (name === 'Notifications') {
                const root = page.locator('#notificationsAdmin [data-notification-settings]');
                const catalog = root.locator('[data-notification-editor] > details').filter({has:page.getByText('Registered notification types',{exact:true})});
                await catalog.locator(':scope > summary').click();
                const plugin = catalog.locator('.notificationPluginCatalog[data-notification-source="account-hub"]');
                await plugin.locator(':scope > summary').click();
                await plugin.locator('.notificationRegisteredType[data-notification-type="*"] > summary').click();
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
                const behavior = type.locator('.notificationTypeBehavior');
                if (!await behavior.evaluate(node => node.open)) await behavior.locator(':scope > summary').click();
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
                const push=root.locator('[data-notification-editor] > section').filter({has:page.getByRole('heading',{name:'Browser notifications',exact:true})});
                const toggle=push.getByLabel('Allow browser notifications',{exact:true});
                assert.equal(await push.locator('input:not([type=checkbox])').count(),0,'Push setup must not require URLs, contacts or keys');
                for(const enabled of [true,false]){
                    const reply=page.waitForResponse(response=>response.request().method()==='PUT'&&response.url().endsWith('/settings/notifications/push'));
                    await push.getByText('Allow browser notifications',{exact:true}).click();
                    const result=await reply;assert.equal(result.status(),200);assert.equal((await result.json()).ok,true);
                    await page.waitForFunction(()=>![...document.querySelectorAll('#notificationsAdmin input[type=checkbox]')].find(e=>e.closest('label')?.textContent.includes('Allow browser notifications'))?.disabled);
                    assert.equal(await toggle.isChecked(),enabled);
                    const persistedPush=await page.evaluate(async()=>{const result=await ApiClient.ajax({url:ApiClient.getUrl('Notifications/api/v1/settings/notifications/push'),type:'GET',dataType:'json'});return result.data;});
                    assert.equal(persistedPush.enabled,enabled);assert(!('privateKey'in persistedPush));
                }
                const deviceDefault=push.getByLabel('Enable on devices by default',{exact:true});
                for(const enabled of [true,false]){
                    const reply=page.waitForResponse(response=>response.request().method()==='PUT'&&response.url().endsWith('/settings/notifications/push'));
                    await push.getByText('Enable on devices by default',{exact:true}).click();assert.equal((await reply).status(),200);
                    await page.waitForFunction(()=>![...document.querySelectorAll('#notificationsAdmin input[type=checkbox]')].find(e=>e.closest('label')?.textContent.includes('Enable on devices by default'))?.disabled);
                    assert.equal(await deviceDefault.isChecked(),enabled);
                }
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
            if (name === 'AutomaticDeletionRequests' || name === 'DeletionQueueAdmin') assert.deepEqual(pageErrors.slice(errorStart), [], name + ': native editor errors');
            const help = page.locator('.suiteAdmin:visible .suiteHelpButton:visible').first(); await help.waitFor();
            for (const width of [320, 1280]) {
                await page.setViewportSize({ width, height: 900 });
                if (name === 'privateSyncPlay') await page.locator('#pspForm[aria-busy="false"] #pspSave:enabled').waitFor();
                await page.waitForFunction(() => {
                    const button = [...document.querySelectorAll('.suiteAdmin .suiteHelpButton')].find(e => e.getClientRects().length);
                    return button && !button.matches(':disabled');
                });
                if (name === 'DiscordBridge') {
                    assert(await page.locator('.discordBridgePage:visible .checkboxContainer').evaluateAll(items => items.every(item => {
                        const label = item.querySelector('label'); return !label || label.getBoundingClientRect().bottom <= item.getBoundingClientRect().bottom + 2;
                    })), name + '/' + width + ': checkbox text escapes its row');
                    const editor = page.locator('.discordBridgePage'), writes = [];
                    const capture = request => { if (request.method() === 'PUT' && request.url().includes('/DiscordBridge/admin/v1/settings')) writes.push(request.url()); };
                    page.on('request', capture);
                    try {
                        await editor.locator('[data-permission="reset"]').waitFor();
                        const nativeActions=await editor.locator('[data-permission]').count();
                        assert.equal(nativeActions, 9);
                        await editor.locator('[data-permission="party.share.channel"]').waitFor();
                        const users = editor.getByLabel('Edit permissions for', { exact: true });
                        const user = await users.locator('option').evaluateAll(options => options.find(option => option.value)?.value);
                        assert(user, 'Installed permission editor needs native user choices');
                        for (const theme of ['dark', 'light']) {
                            await applyTheme(theme); await users.selectOption('');
                            await editor.getByLabel('Create account invitations', { exact: true }).selectOption('deny');
                            await users.selectOption(user);
                            const reset = editor.getByLabel('Request own recovery link', { exact: true });
                            assert.equal(await reset.inputValue(), 'inherit');
                            await reset.selectOption('deny');
                            await editor.getByRole('button', { name: 'Use inherited permissions', exact: true }).click();
                            assert.equal(await reset.inputValue(), 'inherit');
                            await editor.getByRole('button', { name: 'Discard permission edits', exact: true }).click();
                            await reset.focus(); assert(await reset.evaluate(input => input === document.activeElement));
                            assert(await editor.evaluate(root => root.scrollWidth <= root.clientWidth + 2), 'Discord permission editor overflows');
                            assert.equal(writes.length, 0, 'Draft/reset/discard must not write settings');
                            if (process.env.ADMIN_SCREENSHOTS) await editor.locator('fieldset').filter({ has: page.locator('.bridgePermissionRows') }).screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/discord-permissions-' + theme + '-' + width + '.png' });
                            discordPermissions.push({ theme, width, nativeActions, inheritance: true, draftOnly: true, passed: true });
                        }
                    } finally { page.off('request', capture); }
                }
                await help.blur(); await help.focus();
                assert.equal(await help.getAttribute('aria-expanded'), 'true', name + '/' + width + ': keyboard help did not open ' + JSON.stringify(await help.evaluate(e => ({ disabled: e.matches(':disabled'), focused: e === document.activeElement, parent: e.parentElement.outerHTML.slice(0, 900), active: document.activeElement?.outerHTML.slice(0, 300) }))));
                const layout = await page.evaluate(() => ({ width: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth,
                    offenders: [...document.querySelectorAll('*')].filter(element => { const rect = element.getBoundingClientRect(); return rect.width > 0 && rect.height > 0 && rect.right > document.documentElement.clientWidth + 2; }).slice(0, 12).map(element => ({ tag: element.tagName, classes: element.className, width: Math.round(element.getBoundingClientRect().width), right: Math.round(element.getBoundingClientRect().right) })) }));
                if (layout.scroll > layout.width + 2 && process.env.ADMIN_SCREENSHOTS) await page.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/overflow-' + name + '-' + width + '.png', fullPage: true });
                assert(layout.scroll <= layout.width + 2, name + '/' + width + ': admin page overflows ' + JSON.stringify(layout));
                await help.press('Escape'); assert.equal(await help.getAttribute('aria-expanded'), 'false');
                await help.blur(); await help.hover(); assert.equal(await help.getAttribute('aria-expanded'), 'true');
                await page.mouse.move(0, 0);
                if (process.env.ADMIN_SCREENSHOTS) await page.screenshot({ path: process.env.ADMIN_SCREENSHOTS + '/' + name + '-' + width + '.png', fullPage: true });
                if(name==='DeletionQueueAdmin'&&process.env.ADMIN_SCREENSHOTS)await page.locator('.dq-canvas-editor').first().screenshot({path:process.env.ADMIN_SCREENSHOTS+'/overlay-workspace-'+width+'.png'});
                adminPages.push({ name, width, help: true, noOverflow: true });
            }
        }
        console.log(JSON.stringify({ checks: results, notificationPalettes, notificationCategories, notificationSwipes, discordPermissions, customThemeOverride: true, keyboardTabNavigation: true, syncplaySettings: true, adminPages }));
    } catch (error) { throw new Error(phase + ': ' + error.message); }
    finally { await browser.close(); await relay.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
