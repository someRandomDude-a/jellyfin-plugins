// Read-only rendering contract using the immutable upstream Enhanced 12.13 sources.
// No Arr, Seerr or production media calls are made by this fixture.
const assert = require('node:assert/strict');
module.exports = async function checkCalendar(page) {
    const commit = 'ef1ac3c7917421fecf1211249980f037ee524d3d';
    const base = `https://raw.githubusercontent.com/n00bcodr/Jellyfin-Enhanced/${commit}/Jellyfin.Plugin.JellyfinEnhanced/js/arr/calendar/`;
    const modules = await Promise.all(['data', 'render-events', 'render-views', 'actions'].map(async name => {
        const response = await fetch(base + 'calendar-page-' + name + '.js'); assert(response.ok); return response.text();
    }));
    const pattern = '**/DeletionQueue/proposals?*';
    const id = '66666666-6666-6666-6666-666666666666';
    await page.route(pattern, route => route.fulfill({ json: [
        { id, title: 'Dated removal fixture', status: 'Waiting', scheduledFor: '2026-11-05T12:00:00Z' },
        { id: 'blocked', title: 'Unscheduled removal fixture', status: 'Blocked', notBefore: '2026-11-05T12:00:00Z' }
    ] }));
    await page.evaluate(() => {
        window.__dqOriginalEnhanced = window.JellyfinEnhanced;
        const escapeHtml = text => { const node = document.createElement('span'); node.textContent = String(text ?? ''); return node.innerHTML; };
        window.JellyfinEnhanced = { pluginConfig: {}, currentSettings: {}, escapeHtml, t: key => key,
            cdn: { selfhst: () => 'data:image/gif;base64,R0lGODlhAQABAAAAACw=' },
            core: { api: { plugin: async () => ({ events: [] }) } } };
        const container = document.createElement('div'); container.id = 'dq-real-calendar'; container.className = 'dialog je-calendar-page';
        container.style.cssText = 'position:fixed;inset:5rem 1rem 1rem;overflow:auto;z-index:1800;padding:1rem;'; document.body.append(container);
    });
    for (const content of modules) await page.addScriptTag({ content });
    await page.evaluate(() => {
        const JE = window.JellyfinEnhanced, P = JE.internals.calendarPage;
        JE.calendarPage = { renderPage: P.renderPage, toggleFilter: P.toggleFilter };
        P.state.currentDate = new Date(2026, 10, 5); P.state.viewMode = 'month';
        P.state.settings.displayMode = 'list'; P.state.settings.firstDayOfWeek = 'Monday';
        P.state.events = [{ id: 'native-calendar-event', title: 'Native release fixture', releaseDate: '2026-11-05T10:00:00Z', releaseType: 'Episode', type: 'Series', source: 'Sonarr', monitored: true }];
        document.addEventListener('click', P.handleEventClick);
        P.renderPage(document.querySelector('#dq-real-calendar'));
    });
    const calendar = page.locator('#dq-real-calendar');
    await calendar.getByRole('button', { name: 'Dated removal fixture · Deletion Date (earliest)', exact: true }).waitFor();
    assert.equal(await calendar.locator('.dq-calendar-removals').count(), 0);
    assert.equal(await calendar.getByText('Unscheduled removal fixture', { exact: true }).count(), 0);
    for (const view of ['month', 'week', 'day', 'agenda']) for (const display of view === 'agenda' ? ['list'] : ['list', 'cards', 'backdrop']) {
        await page.evaluate(({ view, display }) => { const JE = window.JellyfinEnhanced; JE.internals.calendarPage.state.viewMode = view; JE.internals.calendarPage.state.settings.displayMode = display; JE.calendarPage.renderPage(); }, { view, display });
        await calendar.getByRole('button', { name: 'Dated removal fixture · Deletion Date (earliest)', exact: true }).waitFor();
        assert.equal(await calendar.locator('[data-event-id="native-calendar-event"]').count(), 1, `${view}/${display}: native releases must remain`);
    }
    await calendar.getByRole('button', { name: 'Deletion Date', exact: true }).click();
    await page.waitForFunction(() => !document.querySelector('#dq-real-calendar [data-event-id="native-calendar-event"]'));
    assert.equal(await calendar.locator('.dq-calendar-event').count(), 1);
    await page.evaluate(() => { const JE = window.JellyfinEnhanced, state = JE.internals.calendarPage.state; state.activeFilters.add('Episode'); state.filterMatchMode = 'all'; JE.calendarPage.renderPage(); });
    await page.waitForFunction(() => !document.querySelector('#dq-real-calendar [data-event-id]'));
    await page.evaluate(() => { const JE = window.JellyfinEnhanced; JE.internals.calendarPage.state.filterInvert = true; JE.calendarPage.renderPage(); });
    await calendar.getByRole('button', { name: 'Dated removal fixture · Deletion Date (earliest)', exact: true }).waitFor();
    assert.equal(await calendar.locator('[data-event-id="native-calendar-event"]').count(), 1);
    await page.evaluate(() => {
        const JE = window.JellyfinEnhanced, P = JE.internals.calendarPage;
        document.removeEventListener('click', P.handleEventClick); document.querySelector('#dq-real-calendar').remove();
        window.JellyfinEnhanced = window.__dqOriginalEnhanced; delete window.__dqOriginalEnhanced;
    });
    await page.unroute(pattern);
};
