/* Shared preview/live renderer. Declarative text and geometry only: no HTML, URLs or arbitrary CSS. */
(() => {
    'use strict';
    if (window.NotificationCard) return;
    const defaults = () => ({ height: 180, layers: [
        { id: 'image', kind: 'image', x: 0, y: 0, width: 26, height: 100, text: '', fontSize: 16, fontFamily: 'inherit', fontWeight: 400, align: 'start', tone: 'inherit', fit: 'contain' },
        { id: 'title', kind: 'title', x: 30, y: 6, width: 68, height: 28, text: '', fontSize: 18, fontFamily: 'inherit', fontWeight: 700, align: 'start', tone: 'inherit', fit: 'cover' },
        { id: 'body', kind: 'body', x: 30, y: 38, width: 68, height: 58, text: '', fontSize: 14, fontFamily: 'inherit', fontWeight: 400, align: 'start', tone: 'inherit', fit: 'cover' }
    ] });
    const clamp = (n, low, high) => Math.min(high, Math.max(low, Number.isFinite(Number(n)) ? Number(n) : low));
    function render(root, event, { canvas = event.layout?.canvas, image, simulated = false } = {}) {
        if (!canvas?.layers?.length) canvas = defaults();
        const doc = root.ownerDocument, board = doc.createElement('div'); board.className = 'notificationCanvas'; board.style.height = clamp(canvas.height, 100, 500) + 'px';
        board.setAttribute('aria-label', event.title + '. ' + event.body);
        for (const layer of canvas.layers.slice(0, 12)) {
            if (!['title', 'body', 'text', 'image'].includes(layer.kind)) continue;
            const box = doc.createElement('div'); box.className = 'notificationCanvasLayer'; box.dataset.layer = layer.id;
            const x = clamp(layer.x, 0, 99), y = clamp(layer.y, 0, 99);
            Object.assign(box.style, { left: x + '%', top: y + '%', width: clamp(layer.width, 1, 100 - x) + '%', height: clamp(layer.height, 1, 100 - y) + '%', fontSize: clamp(layer.fontSize, 10, 48) + 'px',
                fontFamily: ['inherit', 'sans-serif', 'serif', 'monospace'].includes(layer.fontFamily) ? layer.fontFamily : 'inherit', fontWeight: [400, 500, 600, 700, 800].includes(layer.fontWeight) ? String(layer.fontWeight) : '400', textAlign: ['start', 'center', 'end'].includes(layer.align) ? layer.align : 'start' });
            box.dataset.tone = ['inherit', 'muted', 'accent'].includes(layer.tone) ? layer.tone : 'inherit';
            if (layer.kind === 'image') {
                box.classList.add('notificationCanvasImage');
                if (image) { const picture = typeof image === 'function' ? image() : image.parentElement?.classList.contains('notificationCanvasImage') ? image.cloneNode() : image; if (picture) { picture.style.objectFit = layer.fit === 'contain' ? 'contain' : 'cover'; box.append(picture); } }
                else if (simulated && event.exampleImage !== false) { const icon=doc.createElement('span');icon.className='material-icons';icon.textContent='image';icon.setAttribute('aria-hidden','true');const caption=doc.createElement('small');caption.textContent='Media image';box.append(icon,caption);box.classList.add('notificationSimulatedImage'); }
            } else {
                const template = layer.text || (layer.kind === 'title' ? '{title}' : layer.kind === 'body' ? '{body}' : 'Text');
                box.textContent = template.replace(/\{(title|body|source|type)\}/g, (_, key) => String(event[key] || '')).slice(0, 4000);
            }
            board.append(box);
        }
        root.replaceChildren(board); return board;
    }
    window.NotificationCard = { render, defaults };
})();
