/* Shared presentation only: no requests, credentials or changes to plugin authorization. */
(() => {
    'use strict';
    if (window.SuiteAdminUI) { window.SuiteAdminUI.mount(); return; }
    const style = document.createElement('style');
    style.textContent = `
    .suiteAdmin { --suite-line: color-mix(in srgb, currentColor 18%, transparent); --suite-surface: color-mix(in srgb, currentColor 4%, transparent); }
    .suiteAdmin .content-primary { max-width: 76rem; margin-inline: auto; padding-bottom: 3rem; }
    .suiteAdmin h1, .suiteAdmin .sectionTitle { letter-spacing: -.025em; }
    .suiteAdmin h2 { margin-block: 1.7rem 1rem; font-size: 1.3em; }
    .suiteAdmin h3 { font-size: 1.08em; }
    .suiteAdmin form, .suiteAdmin [data-notification-settings] > div > fieldset, .suiteAdmin .suiteCard { border: 1px solid var(--suite-line); border-radius: 1rem; padding: clamp(1rem, 3vw, 1.75rem); margin-block: 1rem; background: var(--suite-surface); min-width: 0; }
    .suiteAdmin form form { border: 0; padding: 0; background: none; }
    .suiteAdmin .inputContainer, .suiteAdmin .selectContainer { position: relative; margin-block: 1rem 1.5rem; }
    .suiteAdmin .inputLabel, .suiteAdmin .selectLabel { display: inline-block; width: auto; }
    .suiteAdmin input:not([type=checkbox]):not([type=radio]), .suiteAdmin select, .suiteAdmin textarea { box-sizing: border-box; max-width: 100%; border-radius: .45rem; }
    .suiteAdmin input:focus-visible, .suiteAdmin select:focus-visible, .suiteAdmin textarea:focus-visible, .suiteAdmin button:focus-visible, .suiteAdmin summary:focus-visible { outline: 2px solid var(--theme-primary-color, currentColor); outline-offset: 3px; }
    .suiteAdmin .checkboxContainer { display: block; height: auto; min-height: 1.8em; margin-block: 1rem; }
    .suiteAdmin .emby-checkbox-label { height: auto; min-height: 1.8em; white-space: normal; }
    .suiteAdmin .checkboxContainer .fieldDescription { margin-block: .5rem; }
    .suiteAdmin .hubGrid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 18rem), 1fr)); gap: .5rem 1.5rem; }
    .suiteAdmin button.raised { border-radius: .55rem; margin-block: .3rem; }
    .suiteAdmin [role=status]:empty, .suiteAdmin [role=alert]:empty { display: none; }
    .suiteAdmin [role=status], .suiteAdmin [role=alert] { overflow-wrap: anywhere; }
    .suiteAdmin .hubTabs { display: flex; flex-wrap: wrap; gap: .4rem; border-bottom: 1px solid var(--suite-line); padding-bottom: .8rem; }
    .suiteAdmin .hubTabs button { border-radius: 2rem; }
    .suiteAdmin .hubTabs [aria-selected=true] { box-shadow: inset 0 -3px var(--theme-primary-color, currentColor); }
    .suiteAdmin table { width: 100%; border-collapse: collapse; }
    .suiteAdmin th { text-align: start; font-size: .9em; }
    .suiteAdmin td, .suiteAdmin th { padding: .8rem; border-bottom: 1px solid var(--suite-line); vertical-align: top; }
    .suiteAdmin tbody tr:hover { background: var(--suite-surface); }
    .suiteAdmin details.suiteCard > summary { cursor: pointer; font-weight: 600; padding-block: .3rem; }
    .suiteAdmin pre, .suiteAdmin textarea[data-json] { border: 1px solid var(--suite-line); border-radius: .65rem; padding: 1rem; background: var(--suite-surface); font: .85rem/1.65 ui-monospace, Consolas, monospace; white-space: pre; overflow: auto; max-height: 28rem; tab-size: 2; }
    .suiteAdmin pre code { font: inherit; color: inherit; }
    .suiteAdmin .suiteHelp { display: inline-block; vertical-align: middle; margin-inline-start: .45rem; position: relative; font-size: 1rem; font-weight: normal; letter-spacing: normal; }
    .suiteAdmin .suiteHelpButton { display: inline-grid; place-items: center; padding: 0; margin: 0; min-width: 0; width: 1.4rem; height: 1.4rem; border: 1px solid var(--suite-line); border-radius: 50%; background: transparent; color: inherit; font: inherit; cursor: help; }
    .suiteAdmin .suiteHelpText { position: absolute; z-index: 100; inset-inline-start: 0; inset-block-start: 100%; width: min(20rem, 65vw); box-sizing: border-box; padding: .85rem 1rem; border: 1px solid var(--suite-line); border-radius: .65rem; background: var(--theme-background-color, Canvas); color: var(--theme-text-color, CanvasText); box-shadow: 0 .4rem 1.5rem #0004; font-size: .9rem; line-height: 1.5; white-space: normal; overflow-wrap: anywhere; }
    .suiteAdmin .suiteCodeTools { display: flex; align-items: center; gap: .6rem; flex-wrap: wrap; font-size: .9em; }
    @media (max-width: 600px) { .suiteAdmin .content-primary { padding-inline: 1rem; } .suiteAdmin form { padding: 1rem; } .suiteAdmin .hubGrid { grid-template-columns: minmax(0, 1fr); } }
    @media (prefers-reduced-motion: reduce) { .suiteAdmin * { scroll-behavior: auto; } }
    `;
    document.head.append(style);
    let sequence = 0;
    function help(text, label = 'More information') {
        const wrap = document.createElement('span'), button = document.createElement('button'), tip = document.createElement('span');
        wrap.className = 'suiteHelp'; button.setAttribute('is', 'emby-button'); button.className = 'emby-button button-flat suiteHelpButton'; button.type = 'button'; button.textContent = '?'; button.setAttribute('aria-label', label);
        tip.className = 'suiteHelpText'; tip.id = 'suite-help-' + (++sequence); tip.role = 'tooltip'; tip.textContent = text; tip.hidden = true;
        button.setAttribute('aria-describedby', tip.id); button.setAttribute('aria-expanded', 'false');
        const show = () => {
            tip.hidden = false; button.setAttribute('aria-expanded', 'true'); tip.style.transform = '';
            const box = tip.getBoundingClientRect(), left = Math.max(8, Math.min(box.left, window.innerWidth - box.width - 8));
            tip.style.transform = 'translateX(' + (left - box.left) + 'px)';
        };
        const hide = () => { tip.hidden = true; button.setAttribute('aria-expanded', 'false'); };
        button.onclick = show; wrap.onmouseenter = show; wrap.onmouseleave = () => { if (!wrap.contains(document.activeElement)) hide(); };
        button.onfocus = show; wrap.onfocusout = event => { if (!wrap.contains(event.relatedTarget)) hide(); };
        wrap.onkeydown = event => { if (event.key === 'Escape') { hide(); event.stopPropagation(); } };
        wrap.append(button, tip); return wrap;
    }
    const mounted = new WeakSet();
    function mount() {
        for (const root of document.querySelectorAll('.suiteAdmin')) {
            if (mounted.has(root)) continue; mounted.add(root);
            function enhance() {
                for (const node of root.querySelectorAll('[data-help]')) {
                    const text = node.getAttribute('data-help'); node.removeAttribute('data-help');
                    const label = node.querySelector('label, legend') || node;
                    // Keep help buttons outside labels so they never toggle a checkbox.
                    if (node.matches('input, select, textarea')) (node.closest('label') || node).insertAdjacentElement('afterend', help(text));
                    else if (label !== node) label.insertAdjacentElement('afterend', help(text, 'About ' + label.textContent.trim()));
                    else node.append(help(text));
                }
                for (const node of root.querySelectorAll('[data-help-for]')) {
                    const target = root.querySelector(node.getAttribute('data-help-for'));
                    if (!target) continue;
                    const anchor = target.closest('.inputContainer, .selectContainer, .checkboxContainer') || target;
                    const info = help(node.textContent, 'About ' + (target.labels?.[0]?.textContent.trim() || target.textContent.trim() || 'this setting'));
                    if (anchor.matches('button, label, input, select, textarea')) anchor.insertAdjacentElement('afterend', info); else anchor.append(info);
                    node.remove();
                }
                for (const pre of root.querySelectorAll('pre')) {
                    if (pre.firstElementChild?.tagName === 'CODE' || !pre.textContent.trim()) continue;
                    const code = document.createElement('code'); let value = pre.textContent;
                    try { value = JSON.stringify(JSON.parse(value), null, 2); code.className = 'language-json'; } catch { /* Preserve non-JSON diagnostics verbatim. */ }
                    code.textContent = value; pre.replaceChildren(code); pre.tabIndex = 0; pre.setAttribute('aria-label', pre.getAttribute('aria-label') || 'Details');
                }
                for (const editor of root.querySelectorAll('textarea[data-json]:not([data-code-ready])')) {
                    editor.dataset.codeReady = 'true'; editor.spellcheck = false;
                    const tools = document.createElement('div'), format = document.createElement('button'), result = document.createElement('span');
                    tools.className = 'suiteCodeTools'; format.type = 'button'; format.className = 'emby-button raised'; format.textContent = 'Format JSON'; result.setAttribute('role', 'status');
                    format.onclick = () => { try { editor.value = JSON.stringify(JSON.parse(editor.value), null, 2); editor.dispatchEvent(new Event('input', { bubbles: true })); result.textContent = 'Valid JSON'; } catch { result.textContent = 'Invalid JSON. Check quotes, commas and brackets.'; editor.focus(); } };
                    tools.append(format, result); editor.insertAdjacentElement('afterend', tools);
                }
            }
            const observer = new MutationObserver(enhance);
            const start = () => { enhance(); observer.observe(root, { childList: true, subtree: true }); };
            const stop = () => { observer.disconnect(); for (const tip of root.querySelectorAll('.suiteHelpText')) tip.hidden = true; for (const button of root.querySelectorAll('.suiteHelpButton')) button.setAttribute('aria-expanded', 'false'); };
            root.addEventListener('viewshow', start); root.addEventListener('viewhide', stop); root.addEventListener('viewdestroy', stop); start();
        }
    }
    window.SuiteAdminUI = { mount, help }; mount();
})();
