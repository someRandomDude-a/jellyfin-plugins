/* Bounded, local draft editor. Native PNG rendering remains the authoritative preview/output. */
(() => {
    'use strict';
    if (window.DeletionQueueOverlayEditor) return;
    const get = (value, key) => value?.[key] ?? value?.[key[0].toUpperCase() + key.slice(1)];
    const node = (tag, text) => { const element = document.createElement(tag); if (text !== undefined) element.textContent = text; return element; };
    const clone = value => JSON.parse(JSON.stringify(value));
    const clamp = (n, min, max) => Math.max(min, Math.min(max, n));
    const round = n => Math.round(n * 10000) / 10000;
    function control(parent, label, type, value, options) {
        const row = node('div'); row.className = options ? 'selectContainer' : 'inputContainer';
        row.innerHTML = options ? '<label></label><select is="emby-select"></select>' : type === 'textarea' ? '<label></label><textarea is="emby-textarea" rows="3"></textarea>' : '<label></label><input is="emby-input">';
        const input = row.querySelector('input,select,textarea'); input.id = 'studio-' + crypto.randomUUID(); const caption = row.querySelector('label'); caption.htmlFor = input.id; caption.textContent = label;
        if (options) for (const [id, text] of options) { const option = node('option', text); option.value = id; input.append(option); }
        else if (type !== 'textarea') input.type = type;
        input.value = value ?? ''; parent.append(row); return input;
    }
    function mount(parent, template, portrait) {
        const source = clone(template), base = { text: get(source, 'text'), position: get(source, 'position'), bandHeight: get(source, 'bandHeight'), fontSize: get(source, 'fontSize'), foreground: get(source, 'foreground'), background: get(source, 'background'), opacity: get(source, 'opacity') };
        const supplied = get(source, 'layers');
        const layer = (kind, properties = {}) => ({ id: crypto.randomUUID(), kind, text: kind === 'text' ? 'Removal {date}' : 'Rectangle', x: .1, y: .1, width: .8, height: .16, rotation: 0, color: kind === 'text' ? '#FFFFFF' : '#8B2635', opacity: kind === 'text' ? 255 : 220, fontSize: .055, align: 'center', bold: true, ...properties });
        const normalize = item => Object.fromEntries(['id', 'kind', 'text', 'x', 'y', 'width', 'height', 'rotation', 'color', 'opacity', 'fontSize', 'align', 'bold', 'cornerRadius'].map(key => [key, get(item, key) ?? (key==='cornerRadius'?0:undefined)]));
        let layers = supplied ? supplied.map(normalize) : [
            layer('rectangle', { x: 0, y: base.position === 'top' ? 0 : 1 - base.bandHeight, width: 1, height: base.bandHeight, color: base.background, opacity: base.opacity }),
            layer('text', { x: 0, y: base.position === 'top' ? 0 : 1 - base.bandHeight, width: 1, height: base.bandHeight, text: base.text, color: base.foreground, fontSize: base.fontSize })
        ];
        let selected = layers.at(-1)?.id, history = [], future = [], gesture, disposed = false, snap = true, sampleDate = 'YYYY-MM-DD', snapGuides = [];
        const root = node('div'); root.className = 'dq-canvas-editor';root.dataset.layout=portrait?'portrait':'landscape';root.dataset.zoom='fit'; parent.append(root);
        const toolbar = node('div'); toolbar.className = 'dq-canvas-toolbar'; toolbar.setAttribute('aria-label', 'Overlay editing tools'); root.append(toolbar);
        function button(label, run) { const holder = node('div'); holder.innerHTML = '<button is="emby-button" type="button" class="raised"></button>'; const b = holder.firstElementChild; b.textContent = label; b.onclick = run; toolbar.append(b); return b; }
        const snapshot = () => ({ layers: clone(layers), selected });
        const remember = () => { history.push(snapshot()); if (history.length > 50) history.shift(); future = []; };
        const chosen = () => layers.find(item => item.id === selected);
        const commit = change => { remember(); change(); render(); };
        const addText = button('Add text', () => { if (layers.length < 12) commit(() => { const item = layer('text'); layers.push(item); selected = item.id; }); });
        const addShape = button('Add rectangle', () => { if (layers.length < 12) commit(() => { const item = layer('rectangle'); layers.push(item); selected = item.id; }); });
        const undo = button('Undo', () => { if (!history.length) return; future.push(snapshot()); const state = history.pop(); layers = state.layers; selected = state.selected; render(); });
        const redo = button('Redo', () => { if (!future.length) return; history.push(snapshot()); const state = future.pop(); layers = state.layers; selected = state.selected; render(); });
        const duplicate = button('Duplicate', () => { const item = chosen(); if (item && layers.length < 12) commit(() => { const copy = { ...clone(item), id: crypto.randomUUID(), x: Math.min(item.x + .025, 1 - item.width), y: Math.min(item.y + .025, 1 - item.height) }; layers.push(copy); selected = copy.id; }); });
        const remove = button('Delete layer', () => { if (chosen()) commit(() => { layers = layers.filter(item => item.id !== selected); selected = layers.at(-1)?.id; }); });
        button('Bring forward', () => { const i = layers.findIndex(item => item.id === selected); if (i >= 0 && i < layers.length - 1) commit(() => { [layers[i], layers[i + 1]] = [layers[i + 1], layers[i]]; }); });
        button('Send backward', () => { const i = layers.findIndex(item => item.id === selected); if (i > 0) commit(() => { [layers[i], layers[i - 1]] = [layers[i - 1], layers[i]]; }); });
        const grid = button('Snap: on', () => { snap = !snap; grid.textContent = 'Snap: ' + (snap ? 'on' : 'off'); grid.setAttribute('aria-pressed', String(snap)); canvas.classList.toggle('dq-canvas-grid', snap); }); grid.setAttribute('aria-pressed', 'true');
        const workspace = node('div'); workspace.className = 'dq-canvas-workspace'; root.append(workspace);
        const layerPanel = node('aside'); layerPanel.className = 'dq-canvas-layers'; layerPanel.setAttribute('aria-label', 'Layers'); workspace.append(layerPanel);
        const viewport = node('div'); viewport.className = 'dq-canvas-viewport'; workspace.append(viewport);
        const canvas = node('div'); canvas.className = 'dq-canvas-artboard dq-canvas-grid'; canvas.style.aspectRatio = portrait ? '2 / 3' : '16 / 9'; canvas.tabIndex = 0; canvas.setAttribute('role', 'group'); canvas.setAttribute('aria-label', (portrait ? 'Portrait' : 'Landscape') + ' overlay canvas'); viewport.append(canvas);
        const zoom = control(toolbar, 'Zoom', '', 'fit', [['fit', 'Fit'], ['.5', '50%'], ['1', '100%'], ['1.5', '150%'], ['2', '200%']]); zoom.parentElement.classList.add('dq-canvas-zoom');
        zoom.onchange = () => {root.dataset.zoom=zoom.value; canvas.style.width = zoom.value === 'fit' ? '100%' : (portrait ? 320 : 560) * Number(zoom.value) + 'px'; canvas.style.maxWidth = zoom.value === 'fit' ? '' : 'none'; paint(); };
        const inspector = node('aside'); inspector.className = 'dq-canvas-inspector'; inspector.append(node('h4', 'Layer properties')); workspace.append(inspector);
        const help=node('details');help.className='dq-canvas-help';help.append(node('summary','Canvas help'));const hint = node('p', 'Select a layer, then drag, resize or rotate it. Arrow keys nudge; Shift uses larger steps. Ctrl/Cmd+Z undoes. Server preview confirms the final PNG.'); hint.className = 'dq-art-muted';help.append(hint); root.append(help);
        const inputs = {}, text = control(inspector, 'Layer text', 'textarea', '');text.parentElement.classList.add('dq-property-text'); text.maxLength = 120; text.title='{date} · {days} · {hours} · {quality}. Unknown schedules show pending.';inputs.text = text;
        inputs.cornerRadius=control(inspector,'Corner radius (%)','number',0);inputs.cornerRadius.min=0;inputs.cornerRadius.max=50;inputs.cornerRadius.step=1;
        const coordinates = node('div'); coordinates.className = 'dq-canvas-coordinates'; inspector.append(coordinates);
        for (const [key, label] of [['x', 'X (%)'], ['y', 'Y (%)'], ['width', 'Width (%)'], ['height', 'Height (%)']]) { const input = control(coordinates, label, 'number', 0); input.min = key === 'width' || key === 'height' ? 4 : 0; input.max = 100; input.step = '.1'; inputs[key] = input; }
        inputs.rotation = control(inspector, 'Rotation (°)', 'number', 0); inputs.rotation.min = -180; inputs.rotation.max = 180;
        inputs.fontSize = control(inspector, 'Font size (% of width)', 'number', 5.5); inputs.fontSize.min = 1; inputs.fontSize.max = 16; inputs.fontSize.step = '.1';
        inputs.color = control(inspector, 'Layer color', 'color', '#FFFFFF'); inputs.opacity = control(inspector, 'Opacity', 'range', 255); inputs.opacity.min = 0; inputs.opacity.max = 255;
        inputs.align = control(inspector, 'Text alignment', '', 'center', [['left', 'Left'], ['center', 'Center'], ['right', 'Right']]);
        inputs.bold = control(inspector, 'Font weight', '', 'true', [['true', 'Bold'], ['false', 'Regular']]);
        for (const [key, input] of Object.entries(inputs)) input.addEventListener('change', () => {
            const item = chosen(); if (!item) return;
            commit(() => { if (['x', 'y', 'width', 'height'].includes(key)) { const value = Number(input.value) / 100; if (!Number.isFinite(value)) return; item[key] = clamp(value, key === 'width' || key === 'height' ? .04 : 0, 1); item.width = Math.min(item.width, 1 - item.x); item.height = Math.min(item.height, 1 - item.y); item.x = Math.min(item.x, 1 - Math.max(.04, item.width)); item.y = Math.min(item.y, 1 - Math.max(.04, item.height)); item.width = Math.max(.04, item.width); item.height = Math.max(.04, item.height); }
                else if(key==='cornerRadius')item.cornerRadius=clamp(Number(input.value)/100||0,0,.5);else if (key === 'bold') item.bold = input.value === 'true'; else if (key === 'fontSize') item.fontSize = clamp(Number(input.value) / 100 || .055, .01, .16); else if (key === 'rotation') item.rotation = clamp(Number(input.value) || 0, -180, 180); else if (key === 'opacity') item.opacity = clamp(Math.round(Number(input.value)), 0, 255); else item[key] = input.value;
            });
        });
        const alignTools = node('div'); alignTools.className = 'dq-canvas-align'; inspector.append(alignTools);
        for (const [index,[label, change]] of [['Align left', item => { item.x = 0; }], ['Center horizontally', item => { item.x = (1 - item.width) / 2; }], ['Align right', item => { item.x = 1 - item.width; }], ['Align top', item => { item.y = 0; }], ['Center vertically', item => { item.y = (1 - item.height) / 2; }], ['Align bottom', item => { item.y = 1 - item.height; }]].entries()) { const b = node('button'); b.type = 'button';b.title=label;b.setAttribute('aria-label',label);const icon=node('span',['align_horizontal_left','align_horizontal_center','align_horizontal_right','align_vertical_top','align_vertical_center','align_vertical_bottom'][index]);icon.className='material-icons';icon.setAttribute('aria-hidden','true');b.append(icon); b.onclick = () => { const item = chosen(); if (item) commit(() => change(item)); }; alignTools.append(b); }
        function inspectorValues() { const item = chosen(); for (const [key, input] of Object.entries(inputs)) { const irrelevant=!!item&&(item.kind !== 'text' && ['text', 'fontSize', 'align', 'bold'].includes(key) || key==='cornerRadius'&&item.kind!=='rectangle');input.parentElement.hidden=irrelevant;input.disabled = !item || irrelevant; if (item && document.activeElement !== input) input.value = ['x', 'y', 'width', 'height', 'fontSize','cornerRadius'].includes(key) ? Number(((item[key]||0) * 100).toFixed(2)) : String(item[key]); }for(const b of alignTools.querySelectorAll('button'))b.disabled=!item; }
        function paint() {
            canvas.replaceChildren();
            for (const item of layers) {
                const element = node('div'); element.className = 'dq-canvas-layer'; element.dataset.layer = item.id; element.tabIndex = 0; element.setAttribute('role', 'button'); element.setAttribute('aria-label', item.kind === 'text' ? item.text || 'Text layer' : 'Rectangle layer'); element.setAttribute('aria-pressed', String(item.id === selected));
                Object.assign(element.style, { left: item.x * 100 + '%', top: item.y * 100 + '%', width: item.width * 100 + '%', height: item.height * 100 + '%', transform: 'rotate(' + item.rotation + 'deg)', opacity: item.opacity / 255 });
                if (item.kind === 'rectangle') {element.style.background = item.color;const w=(canvas.clientWidth||320)*item.width,h=(canvas.clientHeight||480)*item.height;element.style.borderRadius=Math.min(w,h)*(item.cornerRadius||0)+'px';}
                else { const remaining=sampleDate?Math.max(0,new Date(sampleDate+'T00:00:00Z')-Date.now()):NaN;const content = node('span', item.text.replaceAll('{date}', sampleDate).replaceAll('{days}',Number.isFinite(remaining)?String(Math.ceil(remaining/86400000)):'pending').replaceAll('{hours}',Number.isFinite(remaining)?String(Math.ceil(remaining/3600000)):'pending').replaceAll('{quality}', 'Standard')); Object.assign(content.style, { color: item.color, fontSize: ((canvas.clientWidth || (portrait ? 320 : 560)) * item.fontSize) + 'px', fontWeight: item.bold ? '700' : '400', textAlign: item.align, width: '94%' }); element.append(content); }
                if (item.id === selected) { element.classList.add('dq-canvas-selected'); for (const kind of ['resize', 'rotate']) { const handle = node('span', kind === 'rotate' ? '↻' : ''); handle.dataset.handle = kind; handle.className = 'dq-canvas-handle dq-canvas-' + kind; handle.setAttribute('aria-hidden', 'true'); element.append(handle); } }
                element.addEventListener('focus', () => { if (selected !== item.id) { selected = item.id; render(); canvas.focus(); } }); element.addEventListener('click', () => { if (selected !== item.id) { selected = item.id; render(); canvas.focus(); } }); canvas.append(element);
            }
            for(const guide of snapGuides){const line=node('div');line.className='dq-snap-guide';line.setAttribute('aria-hidden','true');Object.assign(line.style,guide.axis==='x'?{left:guide.at*100+'%',top:0,height:'100%',width:'1px'}:{top:guide.at*100+'%',left:0,width:'100%',height:'1px'});canvas.append(line);}
        }
        function render() {
            if (disposed) return; paint(); inspectorValues(); layerPanel.replaceChildren(node('h4', 'Layers'));
            for (const item of [...layers].reverse()) { const b = node('button', item.kind === 'text' ? item.text.slice(0, 35) || 'Text layer' : 'Rectangle'); b.type = 'button'; b.setAttribute('aria-pressed', String(item.id === selected)); b.onclick = () => { selected = item.id; render(); }; layerPanel.append(b); }
            if (!layers.length) layerPanel.append(node('p', 'Add text or a rectangle to begin.'));
            undo.disabled = !history.length; redo.disabled = !future.length; duplicate.disabled = !chosen() || layers.length >= 12; remove.disabled = !chosen(); addText.disabled = addShape.disabled = layers.length >= 12;
        }
        canvas.addEventListener('pointerdown', event => {
            const element = event.target.closest('[data-layer]'); if (!element || event.button !== 0) return; const item = layers.find(layer => layer.id === element.dataset.layer); if (!item) return;
            remember(); selected = item.id; gesture = { pointer: event.pointerId, x: event.clientX, y: event.clientY, original: clone(item), rect: canvas.getBoundingClientRect(), kind: event.target.dataset.handle || 'move' }; canvas.focus(); canvas.setPointerCapture?.(event.pointerId); event.preventDefault(); render();
        });
        canvas.addEventListener('pointermove', event => {
            if (!gesture || gesture.pointer !== event.pointerId) return; const item = chosen(), g = gesture; if (!item || !g.rect.width || !g.rect.height) return;
            const dx = (event.clientX - g.x) / g.rect.width, dy = (event.clientY - g.y) / g.rect.height, quantize = n => snap && !event.altKey ? Math.round(n / .025) * .025 : n;
            if (g.kind === 'resize') { item.width = round(clamp(quantize(g.original.width + dx), .04, 1 - item.x)); item.height = round(clamp(quantize(g.original.height + dy), .04, 1 - item.y)); }
            else if (g.kind === 'rotate') { let angle = Math.atan2(event.clientY - (g.rect.top + (item.y + item.height / 2) * g.rect.height), event.clientX - (g.rect.left + (item.x + item.width / 2) * g.rect.width)) * 180 / Math.PI + 90; if (angle > 180) angle -= 360; item.rotation = Math.round(event.shiftKey ? angle / 15 : angle) * (event.shiftKey ? 15 : 1); }
            else {
                snapGuides=[];
                const align=(value,size,axis)=>{if(!snap||event.altKey)return value;const anchors=[0,.5,1,...layers.filter(l=>l.id!==item.id).flatMap(l=>axis==='x'?[l.x,l.x+l.width/2,l.x+l.width]:[l.y,l.y+l.height/2,l.y+l.height])];let best=6/(axis==='x'?g.rect.width:g.rect.height),result=value,at=null;for(const anchor of anchors)for(const offset of [0,size/2,size]){const delta=anchor-(value+offset);if(Math.abs(delta)<best){best=Math.abs(delta);result=value+delta;at=anchor;}}if(at!==null)snapGuides.push({axis,at});return result;};
                item.x = round(clamp(align(quantize(g.original.x + dx),item.width,'x'), 0, 1 - item.width)); item.y = round(clamp(align(quantize(g.original.y + dy),item.height,'y'), 0, 1 - item.height));
            }
            paint(); inspectorValues();
        });
        const finish = () => { gesture = null;snapGuides=[]; render(); }; canvas.addEventListener('pointerup', finish); canvas.addEventListener('pointercancel', finish);
        root.addEventListener('keydown', event => {
            if (event.target.matches('input,select,textarea')) return;
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') { event.preventDefault(); (event.shiftKey ? redo : undo).click(); return; }
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') { event.preventDefault(); redo.click(); return; }
            if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); remove.click(); return; }
            const item = chosen(), delta = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
            if (item && delta) { event.preventDefault(); commit(() => { const step = event.shiftKey ? .01 : .001; item.x = round(clamp(item.x + delta[0] * step, 0, 1 - item.width)); item.y = round(clamp(item.y + delta[1] * step, 0, 1 - item.height)); }); }
        });
        const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(paint) : null; observer?.observe(canvas); render();
        return { read: () => ({ ...base, layers: clone(layers) }), setSample: date => { sampleDate = date; paint(); }, dispose: () => { disposed = true; gesture = null; history = []; future = []; observer?.disconnect(); root.remove(); } };
    }
    window.DeletionQueueOverlayEditor = { mount };
})();
