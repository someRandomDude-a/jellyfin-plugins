// Personal settings on an owned disposable server; no browser/OS permission prompts.
const assert = require('node:assert/strict');

module.exports = async function checkNotificationCategories(page, applyTheme, screenshots) {
    const inbox = page.locator('#account-hub-inbox'), checks = [];
    const original = await page.evaluate(async () => {
        const response = await fetch(ApiClient.getUrl('Notifications/api/v1/notifications/preferences'), { headers: { Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"' } });
        if (!response.ok) throw Error('Personal preferences unavailable');
        return (await response.json()).data.preferences;
    });
    const waitSwitch = async (name, value) => page.waitForFunction(({ name, value }) => document.querySelector('[aria-label="' + name + '"]')?.getAttribute('aria-checked') === String(value), { name, value });
    try {
        for (const theme of ['dark', 'light']) for (const width of [320, 1280]) {
            await applyTheme(theme); await page.setViewportSize({ width, height: 900 });
            for (const name of ['New episodes and movies', 'Library categories']) {
                const summary = inbox.getByText(name, { exact: true });
                if (!await summary.evaluate(node => node.closest('details').open)) await summary.click();
            }
            const watch = page.getByRole('switch', { name: 'Watchlisted media on in-app', exact: true });
            if (await watch.getAttribute('aria-checked') === 'true') { await watch.click(); await waitSwitch('Watchlisted media on in-app', false); }
            await watch.click(); await waitSwitch('Watchlisted media on in-app', true);
            const favorite = page.getByRole('switch', { name: 'Favorited media on in-app', exact: true });
            if (await favorite.getAttribute('aria-checked') !== 'true') { await favorite.click(); await waitSwitch('Favorited media on in-app', true); }
            for (const toggle of [watch, favorite]) assert(await toggle.evaluate(node => { const box = node.getBoundingClientRect(); return box.width >= 44 && box.height >= 44; }), 'Category switches need a usable mobile touch target');
            for (const name of ['Watchlisted media · In-app', 'Favorited media · In-app']) {
                const summary = inbox.getByText(name, { exact: true });
                if (!await summary.evaluate(node => node.closest('details').open)) await summary.click();
            }
            const important = page.getByRole('radio', { name: 'Watchlisted media on in-app Important', exact: true });
            await important.check();
            await page.waitForFunction(() => document.querySelector('[aria-label="Watchlisted media on in-app Important"]')?.checked && !document.querySelector('[aria-label="Watchlisted media on in-app Important"]')?.disabled);
            // Native radio keyboard behavior and async save must preserve focus.
            await important.focus(); await important.press('ArrowRight');
            await page.waitForFunction(() => document.querySelector('[aria-label="Watchlisted media on in-app Silent"]')?.checked && !document.querySelector('[aria-label="Watchlisted media on in-app Silent"]')?.disabled);
            assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), 'Watchlisted media on in-app Silent');
            await page.getByRole('radio', { name: 'Watchlisted media on in-app Silent', exact: true }).press('ArrowLeft');
            await page.waitForFunction(() => document.querySelector('[aria-label="Watchlisted media on in-app Important"]')?.checked && !document.querySelector('[aria-label="Watchlisted media on in-app Important"]')?.disabled);
            await page.getByRole('radio', { name: 'Favorited media on in-app Silent', exact: true }).check();
            await page.waitForFunction(() => document.querySelector('[aria-label="Favorited media on in-app Silent"]')?.checked && !document.querySelector('[aria-label="Favorited media on in-app Silent"]')?.disabled);
            assert(await inbox.evaluate(node => node.scrollWidth <= node.clientWidth + 1), 'Personal category settings must fit the native panel at ' + width);
            if (screenshots) await inbox.screenshot({ path: screenshots + '/notification-categories-' + theme + '-' + width + '.png' });
            checks.push({ theme, width, watchlisted: true, favorited: true, personalImportance: true, nativeKeyboard: true });
        }
    } catch (error) {
        const state = await inbox.evaluate(node => ({ hidden: node.hidden, settings: !!node.querySelector('.notificationCategorySettings'), radios: node.querySelectorAll('input[type=radio]').length, open: [...node.querySelectorAll('details[open] > summary')].map(summary => summary.textContent) }));
        if (screenshots) await page.screenshot({ path: screenshots + '/notification-categories-failure.png' });
        throw Error(error.message + ' Category state: ' + JSON.stringify(state));
    } finally {
        await page.evaluate(async preferences => {
            const response = await fetch(ApiClient.getUrl('Notifications/api/v1/notifications/preferences'), { method: 'PUT', headers: { Authorization: 'MediaBrowser Token="' + ApiClient.accessToken() + '"', 'Content-Type': 'application/json' }, body: JSON.stringify(preferences) });
            if (!response.ok) throw Error('Could not restore disposable personal preferences');
        }, original);
    }
    return checks;
};
