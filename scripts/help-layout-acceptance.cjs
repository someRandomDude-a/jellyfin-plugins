// Real browser layout only; no server, credentials, external pages or API calls.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright');
(async () => {
    const browser = await chromium.launch({ headless: true });
    const results = [];
    try {
        for (const width of [320, 390, 1280]) for (const direction of ['ltr', 'rtl']) {
            const page = await browser.newPage({ viewport: { width, height: 900 } });
            await page.setContent('<style>body{margin:0}main{position:relative;width:100%;height:200px}.anchor{position:absolute;right:16px;top:20px}</style><main class="suiteAdmin" dir="' + direction + '"><span class="anchor"></span></main>');
            await page.addScriptTag({ content: fs.readFileSync('shared/Web/admin-ui.js', 'utf8') });
            await page.evaluate(() => document.querySelector('.anchor').append(SuiteAdminUI.help('A long description must remain readable without making a narrow native settings page horizontally scrollable.', 'Layout help')));
            await page.getByRole('button', { name: 'Layout help', exact: true }).focus();
            const layout = await page.evaluate(() => {
                const box = document.querySelector('.suiteHelpText').getBoundingClientRect();
                return { viewport: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth, left: box.left, right: box.right };
            });
            results.push({ width, direction, ...layout });
            console.log(JSON.stringify(results.at(-1)));
            assert(layout.left >= 7 && layout.right <= width - 7, 'Help must be clamped visually');
            assert(layout.scroll <= layout.viewport + 2, 'Help must not expand document scroll overflow');
            await page.getByRole('button', { name: 'Layout help', exact: true }).press('Escape');
            assert(await page.getByRole('tooltip').isHidden());
            await page.close();
        }
        console.log('PASS six real-browser help layout/keyboard cases.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
