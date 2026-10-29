(() => {
    'use strict';
    if (window.NotificationStudio) return;
    const copy = value => JSON.parse(JSON.stringify(value));
    const bounded = (value, max, min = 0) => Math.max(min, Math.min(max, Math.round(value * 100) / 100));
    const anchors = [0, .5, 1];
    function snapGeometry(layer, others, pixels, { mode = 'move', enabled = true, threshold = 6 } = {}) {
        const next = { ...layer }, guides = [];
        if (!enabled) return { layer: next, guides };
        const targets = [{ id: null, x: 0, y: 0, width: 100, height: 100 }, ...others.filter(target => target.id !== layer.id)];
        for (const [axis, size] of [['x', 'width'], ['y', 'height']]) {
            const scale = pixels[size] / 100;
            if (!Number.isFinite(scale) || scale <= 0) continue;
            const candidates = [];
            for (const target of targets) for (const anchor of anchors) for (const own of mode === 'resize' ? [1] : anchors) {
                // Centers align with centers; edges may align or meet each other.
                if (mode !== 'resize' && (own === .5) !== (anchor === .5)) continue;
                const position = target[axis] + target[size] * anchor;
                const value = mode === 'resize' ? position - layer[axis] : position - layer[size] * own;
                const minimum = mode === 'resize' ? 1 : 0, maximum = mode === 'resize' ? 100 - layer[axis] : 100 - layer[size];
                const distance = Math.abs(value - (mode === 'resize' ? layer[size] : layer[axis])) * scale;
                if (value < minimum || value > maximum || distance > threshold + 1e-7) continue;
                candidates.push({ value, distance, position, targetId: target.id, kind: anchor === .5 ? 'center' : 'edge' });
            }
            candidates.sort((a, b) => a.distance - b.distance || Number(b.kind === 'center') - Number(a.kind === 'center') || Number(a.targetId === null) - Number(b.targetId === null) || a.position - b.position);
            const best = candidates[0];
            if (best) {
                next[mode === 'resize' ? size : axis] = bounded(best.value, mode === 'resize' ? 100 - layer[axis] : 100 - layer[size], mode === 'resize' ? 1 : 0);
                guides.push({ axis, position: best.position, targetId: best.targetId, kind: best.kind });
            }
        }
        return { layer: next, guides };
    }
    function alignGeometry(layer, reference, axis, alignment) {
        const size = axis === 'x' ? 'width' : 'height', anchor = { start: 0, center: .5, end: 1 }[alignment];
        const target = reference || { id: null, x: 0, y: 0, width: 100, height: 100 };
        const position = target[axis] + target[size] * anchor, wanted = position - layer[size] * anchor;
        const value = bounded(wanted, 100 - layer[size]), clamped = Math.abs(value - wanted) > .0051;
        return { layer: { ...layer, [axis]: value }, clamped, guides: clamped ? [] : [{ axis, position, targetId: target.id, kind: anchor === .5 ? 'center' : 'edge' }] };
    }
    window.NotificationStudio = {
        geometry: Object.freeze({ snap: snapGeometry, align: alignGeometry }),
        mount(root, example, initial, changed) {
            const doc = root.ownerDocument;
            const create = (tag, text, className) => { const node = doc.createElement(tag); if (text !== undefined) node.textContent = text; if (className) node.className = className; return node; };
            let model = copy(initial || window.NotificationCard.defaults()), saved = copy(model);
            let selected = (model.layers.find(layer => layer.kind === 'title') || model.layers[0]).id;
            let history = [], redo = [], alive = true, cancelDrag, inspectorModel, inspectorSelection, inspectorOrder;
            let snapping = true, alignmentReference = '', alignmentSelection, alignmentOrder, guideKey;
            const buttons = new Map(), snapshot = () => ({ model: copy(model), selected });
            const header = create('div', undefined, 'notificationStudioHeader'), heading = create('div');
            heading.append(create('h4', 'Design notification'), create('p', 'Preview only · nothing is sent', 'notificationStudioHint'));
            const status = create('p', undefined, 'notificationStudioStatus'); status.setAttribute('role', 'status');
            header.append(heading, status);
            const toolbar = create('div', undefined, 'notificationStudioToolbar'); toolbar.setAttribute('role', 'toolbar'); toolbar.setAttribute('aria-label', 'Notification design tools');
            const workspace = create('div', undefined, 'notificationStudioWorkspace');
            const stage = create('section', undefined, 'notificationStudioStage'), stageHeader = create('div', undefined, 'notificationStudioStageHeader');
            stageHeader.append(create('h5', 'Canvas'), create('span', 'Drag to move · corner to resize'));
            const board = create('div', undefined, 'notificationStudioBoard'); board.tabIndex=0;board.setAttribute('aria-label','Notification design canvas');
            const shortcuts = create('p', 'Arrow keys move · Shift for larger steps · Ctrl+Z undo · Ctrl+Y redo · Alt bypasses snapping', 'notificationStudioHint');
            const guideRoot=create('div',undefined,'notificationStudioGuides');guideRoot.setAttribute('aria-hidden','true');
            const snapStatus=create('p',undefined,'notificationStudioSnapStatus notificationStudioHint');snapStatus.setAttribute('role','status');
            stage.append(stageHeader, board, snapStatus, shortcuts);
            const inspector = create('aside', undefined, 'notificationStudioInspector'); inspector.setAttribute('aria-label', 'Layer inspector');
            const layers = create('div', undefined, 'notificationStudioLayers'), fields = create('div', undefined, 'notificationStudioFields');
            const alignmentPanel=create('details',undefined,'notificationStudioAlignment'),alignmentControls=create('div',undefined,'notificationStudioAlignControl');
            alignmentPanel.append(create('summary','Align edges & centers'),alignmentControls);
            const horizontal=create('div',undefined,'notificationStudioAlignButtons'),vertical=create('div',undefined,'notificationStudioAlignButtons');
            horizontal.setAttribute('role','group');horizontal.setAttribute('aria-label','Horizontal alignment');vertical.setAttribute('role','group');vertical.setAttribute('aria-label','Vertical alignment');
            alignmentPanel.append(horizontal,vertical);
            inspector.append(create('h5', 'Layers'), layers, alignmentPanel, create('h5', 'Layer properties'), fields); workspace.append(stage, inspector);
            const hint = create('p', 'Use {title}, {body}, {source} or {type} in text. Images use authorized media artwork. Recipients, actions and permissions cannot be changed in the designer.', 'notificationStudioHint');
            root.classList.add('notificationStudio'); root.setAttribute('aria-label', 'Notification designer');
            root.replaceChildren(header, toolbar, workspace, hint);

            function checkpoint() { history.push(snapshot()); if (history.length > 30) history.shift(); redo = []; }
            function edit(update, focusLayer = false) {
                if(!alive)return;const before=snapshot();update();if(JSON.stringify(before.model)===JSON.stringify(model))return;
                history.push(before);if(history.length>30)history.shift();redo=[];commit(focusLayer);
            }
            function updateStatus() {
                const dirty = JSON.stringify(model) !== JSON.stringify(saved);
                root.dataset.dirty = String(dirty); const message=dirty ? 'Unsaved changes · Save layout to apply' : 'Current layout · no unsaved changes';if(status.textContent!==message)status.textContent=message;
                for (const kind of ['title', 'body', 'text', 'image']) buttons.get('Add ' + kind).disabled = model.layers.length >= 12;
                buttons.get('Duplicate layer').disabled = model.layers.length >= 12;
                buttons.get('Remove layer').disabled = model.layers.length <= 1;
                const index = model.layers.findIndex(layer => layer.id === selected);
                buttons.get('Move layer forward').disabled = index === model.layers.length - 1;
                buttons.get('Move layer backward').disabled = index <= 0;
            }
            function commit(focusLayer = false) { if (!alive) return; changed(copy(model)); render(focusLayer); }
            function button(label, action, group) {
                const control = create('button', label); control.type = 'button'; control.setAttribute('is', 'emby-button'); control.className = 'emby-button raised';
                control.onclick = () => { if (alive && !control.disabled) action(); }; group.append(control); buttons.set(label, control);
            }
            const insert = create('div', undefined, 'notificationStudioToolGroup'), arrange = create('div', undefined, 'notificationStudioToolGroup');
            insert.setAttribute('aria-label', 'Insert layer'); arrange.setAttribute('aria-label', 'Arrange layer'); toolbar.append(insert, arrange);
            for (const kind of ['title', 'body', 'text', 'image']) button('Add ' + kind, () => {
                if (model.layers.length >= 12) return; checkpoint();
                const template = window.NotificationCard.defaults().layers[kind === 'image' ? 0 : kind === 'title' ? 1 : 2];
                const layer = { ...template, id: crypto.randomUUID(), kind, text: kind === 'text' ? 'Your text' : '' };
                model.layers.push(layer); selected = layer.id; commit(true);
            }, insert);
            button('Duplicate layer', () => {
                if (model.layers.length >= 12) return; checkpoint();
                const original = model.layers.find(layer => layer.id === selected), duplicate = { ...original, id: crypto.randomUUID(), x: bounded(original.x + 2, 100 - original.width), y: bounded(original.y + 2, 100 - original.height) };
                model.layers.push(duplicate); selected = duplicate.id; commit(true);
            }, arrange);
            function reorder(direction) {
                const index = model.layers.findIndex(layer => layer.id === selected), next = index + direction;
                if (next < 0 || next >= model.layers.length) return; checkpoint();
                [model.layers[index], model.layers[next]] = [model.layers[next], model.layers[index]]; commit(true);
            }
            button('Move layer backward', () => reorder(-1), arrange); button('Move layer forward', () => reorder(1), arrange);
            function removeLayer() {
                if (model.layers.length <= 1) return; checkpoint(); const index = model.layers.findIndex(layer => layer.id === selected);
                model.layers.splice(index, 1); selected = model.layers[Math.min(index, model.layers.length - 1)].id; commit(true);
            }
            button('Remove layer', removeLayer, arrange);
            button('Reset studio', () => edit(() => { model = window.NotificationCard.defaults(); selected = model.layers.find(layer => layer.kind === 'title').id; },true), arrange);
            button('Snap to layers',()=>{snapping=!snapping;buttons.get('Snap to layers').setAttribute('aria-pressed',String(snapping));clearGuides();},arrange);
            buttons.get('Snap to layers').setAttribute('aria-pressed','true');buttons.get('Snap to layers').title='Snap to layer and card edges/centers. Hold Alt to bypass.';
            for(const [label,text,axis,anchor] of [['Align left','Left','x','start'],['Center horizontally','Center','x','center'],['Align right','Right','x','end'],['Align top','Top','y','start'],['Center vertically','Middle','y','center'],['Align bottom','Bottom','y','end']]){
                button(label,()=>{
                    const layer=model.layers.find(value=>value.id===selected),reference=model.layers.find(value=>value.id===alignmentReference);
                    const result=alignGeometry(layer,reference,axis,anchor);edit(()=>Object.assign(layer,result.layer),true);renderGuides(result.guides);
                    if(result.clamped)snapStatus.textContent='Card bounds prevent this alignment.';
                },axis==='x'?horizontal:vertical);
                const control=buttons.get(label);control.textContent=text;control.setAttribute('aria-label',label);control.title=label;
            }

            function renderGuides(guides) {
                const key=JSON.stringify(guides);if(key===guideKey)return;guideKey=key;guideRoot.replaceChildren();
                const descriptions=[];
                for(const guide of guides){
                    const line=create('div',undefined,'notificationStudioGuide');line.dataset.axis=guide.axis;line.dataset.kind=guide.kind;line.style[guide.axis==='x'?'left':'top']=guide.position+'%';guideRoot.append(line);
                    const target=model.layers.find(layer=>layer.id===guide.targetId),name=target?target.kind[0].toUpperCase()+target.kind.slice(1):'Card';descriptions.push(name+' '+guide.kind);
                }
                snapStatus.textContent=descriptions.length?[...new Set(descriptions)].join(' · '):snapping?'Snapping on · Alt for free placement':'Free placement';
            }
            function clearGuides(){guideKey=undefined;renderGuides([]);}
            function renderAlignment(order){
                if(alignmentReference===selected||!model.layers.some(layer=>layer.id===alignmentReference))alignmentReference='';
                if(alignmentSelection===selected&&alignmentOrder===order)return;
                alignmentSelection=selected;alignmentOrder=order;alignmentControls.replaceChildren();
                input('Align relative to',alignmentReference,[{value:'',label:'Card'},...model.layers.map((layer,index)=>({value:layer.id,label:(index+1)+'. '+layer.kind[0].toUpperCase()+layer.kind.slice(1)})).filter(choice=>choice.value!==selected)],value=>{alignmentReference=value;},null,true,alignmentControls);
            }

            function input(label, value, choices, update, bounds, selectionOnly = false, container = fields) {
                const wrap = create('div'), caption = create('label', label), control = create(choices ? 'select' : 'input');
                wrap.className = choices ? 'selectContainer' : 'inputContainer'; caption.className = choices ? 'selectLabel' : 'inputLabel';
                // Jellyfin setters may run before connection: bind the label first.
                control.labelElement = caption; control.id = 'notification-studio-' + crypto.randomUUID(); caption.htmlFor = control.id;
                control.dataset.studioField = label; control.setAttribute('is', choices ? 'emby-select' : 'emby-input'); control.className = choices ? 'emby-select emby-select-withcolor' : 'emby-input';
                if (choices) for (const choice of choices) { const option = create('option', typeof choice === 'object' ? choice.label : String(choice)); option.value = typeof choice === 'object' ? choice.value : choice; control.append(option); }
                else { control.type = typeof value === 'number' ? 'number' : 'text'; if (bounds) { control.required=true;control.min = bounds[0]; control.max = bounds[1]; control.step = bounds[2] || 1; } else control.maxLength = 400; }
                control.value = value;
                control.onchange = () => {
                    if (!alive || !control.reportValidity()) return;
                    const next = typeof value === 'number' ? Number(control.value) : control.value;
                    if (selectionOnly) { update(next); return; }
                    edit(()=>update(next));
                };
                wrap.append(caption, control); container.append(wrap);
                if (choices) { const arrow = create('div', undefined, 'selectArrowContainer'); arrow.setAttribute('aria-hidden', 'true'); arrow.append(create('span', undefined, 'selectArrow material-icons keyboard_arrow_down')); wrap.append(arrow); }
                control.addEventListener('focus', () => caption.classList.add(choices ? 'selectLabelFocused' : 'inputLabelFocused'));
                control.addEventListener('blur', () => caption.classList.remove(choices ? 'selectLabelFocused' : 'inputLabelFocused'));
                if (label === 'Selected layer' || label === 'Text template' || label === 'Font family') wrap.classList.add('notificationStudioFullField');
            }
            function renderInspector() {
                const focused = fields.contains(doc.activeElement) ? doc.activeElement.dataset.studioField : null;
                layers.replaceChildren();
                for (const layer of [...model.layers].reverse()) {
                    const control = create('button'); control.type = 'button'; control.className = 'notificationStudioLayer'; control.dataset.selected = String(layer.id === selected); control.setAttribute('aria-pressed', String(layer.id === selected));
                    control.append(create('span', layer.kind === 'image' ? 'image' : 'title', 'material-icons'), create('span', layer.kind[0].toUpperCase() + layer.kind.slice(1)), create('small', layer.text || (layer.kind === 'title' ? '{title}' : layer.kind === 'body' ? '{body}' : layer.kind === 'image' ? 'Media artwork' : 'Text')));
                    control.firstChild.setAttribute('aria-hidden', 'true'); control.setAttribute('aria-label', 'Select ' + layer.kind + ' layer'); control.onclick = () => {select(layer.id);layers.querySelector('[data-selected=true]')?.focus();}; layers.append(control);
                }
                const layer = model.layers.find(value => value.id === selected), order=model.layers.map(value=>value.id).join('|');
                renderAlignment(order);
                if(inspectorModel===model&&inspectorSelection===selected&&inspectorOrder===order){
                    // Keep native input nodes/focus/Tab order while editing. A new
                    // model (undo/redo) or selected layer rebuilds their bindings.
                    const values={'Selected layer':selected,'Card height':model.height,'Left (%)':layer.x,'Top (%)':layer.y,'Width (%)':layer.width,'Height (%)':layer.height,'Text template':layer.text||'','Font size':layer.fontSize,'Font family':layer.fontFamily,'Font weight':layer.fontWeight,'Text alignment':layer.align,'Text colour':layer.tone,'Image fit':layer.fit};
                    for(const control of fields.querySelectorAll('[data-studio-field]'))if(control.value!==String(values[control.dataset.studioField]))control.value=values[control.dataset.studioField];
                    return;
                }
                inspectorModel=model;inspectorSelection=selected;inspectorOrder=order;
                fields.replaceChildren();
                input('Selected layer', selected, model.layers.map((layer, index) => ({ value: layer.id, label: (index + 1) + '. ' + layer.kind })), select, null, true);
                input('Card height', model.height, null, value => { model.height = value; }, [100, 500]);
                for (const [key, label] of [['x', 'Left (%)'], ['y', 'Top (%)'], ['width', 'Width (%)'], ['height', 'Height (%)']]) input(label, layer[key], null, value => {
                    layer[key] = value; layer.x = bounded(layer.x, 100 - layer.width); layer.y = bounded(layer.y, 100 - layer.height);
                }, [key === 'x' || key === 'y' ? 0 : 1, key === 'x' || key === 'y' ? 99 : 100, 'any']);
                if (layer.kind === 'image') input('Image fit', layer.fit, ['cover', 'contain'], value => { layer.fit = value; });
                else {
                    input('Text template', layer.text || '', null, value => { layer.text = value; });
                    input('Font size', layer.fontSize, null, value => { layer.fontSize = value; }, [10, 48]);
                    input('Font family', layer.fontFamily, ['inherit', 'sans-serif', 'serif', 'monospace'], value => { layer.fontFamily = value; });
                    input('Font weight', layer.fontWeight, [400, 500, 600, 700, 800], value => { layer.fontWeight = value; });
                    input('Text alignment', layer.align, ['start', 'center', 'end'], value => { layer.align = value; });
                    input('Text colour', layer.tone, ['inherit', 'muted', 'accent'], value => { layer.tone = value; });
                }
                if (focused) [...fields.querySelectorAll('[data-studio-field]')].find(control => control.dataset.studioField === focused)?.focus();
            }
            function select(id) { if (!alive || !model.layers.some(layer => layer.id === id)) return; selected = id; clearGuides();renderSelection(); renderInspector(); updateStatus(); }
            function renderSelection() {
                for (const box of board.querySelectorAll('[data-layer]')) {
                    const active = box.dataset.layer === selected; box.dataset.selected = String(active);
                    const layer = model.layers.find(value => value.id === box.dataset.layer); box.setAttribute('aria-label', layer.kind + ' layer' + (active ? ', selected' : ''));
                    box.querySelector('.notificationStudioResize')?.remove();
                    if (active) {
                        const handle = create('button', '↘', 'notificationStudioResize'); handle.type = 'button'; handle.setAttribute('aria-label', 'Resize selected layer');
                        handle.onpointerdown = event => beginDrag(event, box, handle, true);
                        handle.onkeydown = event => { event.stopPropagation(); nudge(event, box, true); };
                        box.append(handle);
                    }
                }
            }
            function nudge(event, box, resize = false) {
                if(!alive)return;
                const movement = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
                if (!movement) return; event.preventDefault(); selected = box.dataset.layer;
                const layer = model.layers.find(value => value.id === selected), step = event.shiftKey ? 10 : 1;
                edit(()=>{if (resize) { layer.width = bounded(layer.width + movement[0] * step, 100 - layer.x, 1); layer.height = bounded(layer.height + movement[1] * step, 100 - layer.y, 1); }
                else { layer.x = bounded(layer.x + movement[0] * step, 100 - layer.width); layer.y = bounded(layer.y + movement[1] * step, 100 - layer.height); }},true);
                if (resize) board.querySelector('.notificationStudioResize')?.focus();
            }
            function beginDrag(event, box, target, resize) {
                if (!alive || event.button !== 0 || event.isPrimary === false) return;
                if (!resize && event.target.closest('.notificationStudioResize')) return;
                event.preventDefault(); event.stopPropagation(); cancelDrag?.(); if (!box.isConnected) return;
                if (selected !== box.dataset.layer) select(box.dataset.layer);
                (resize?target:box).focus();
                const layer = model.layers.find(value => value.id === selected), canvas = box.parentElement, rect = canvas.getBoundingClientRect();
                if (!rect.width || !rect.height) return;
                const before = snapshot(), origin = { ...layer, px: event.clientX, py: event.clientY }; let moved = false;
                try { target.setPointerCapture?.(event.pointerId); } catch { /* Keep direct controls usable without pointer capture. */ }
                const finish = cancelled => {
                    target.onpointermove = target.onpointerup = target.onpointercancel = target.onlostpointercapture = null; cancelDrag = null;
                    if (!alive) return;
                    clearGuides();
                    if (cancelled) { model = before.model; selected = before.selected; render(true); }
                    else if (moved && JSON.stringify(model) !== JSON.stringify(before.model)) { history.push(before); if (history.length > 30) history.shift(); redo = []; commit(true); }
                };
                cancelDrag = () => finish(true);
                target.onpointermove = move => {
                    if (move.pointerId !== event.pointerId || !alive) return;
                    const dx = move.clientX - origin.px, dy = move.clientY - origin.py;
                    if (!moved && Math.hypot(dx, dy) < 2) return; moved = true;
                    if (resize) { layer.width = bounded(origin.width + dx * 100 / rect.width, 100 - layer.x, 1); layer.height = bounded(origin.height + dy * 100 / rect.height, 100 - layer.y, 1); }
                    else { layer.x = bounded(origin.x + dx * 100 / rect.width, 100 - layer.width); layer.y = bounded(origin.y + dy * 100 / rect.height, 100 - layer.height); }
                    const snapped=snapGeometry(layer,model.layers,{width:rect.width,height:rect.height},{mode:resize?'resize':'move',enabled:snapping&&!move.altKey});Object.assign(layer,snapped.layer);renderGuides(snapped.guides);
                    Object.assign(box.style, { left: layer.x + '%', top: layer.y + '%', width: layer.width + '%', height: layer.height + '%' });
                };
                target.onpointerup = up => { if (up.pointerId === event.pointerId) finish(false); };
                target.onpointercancel = target.onlostpointercapture = () => finish(true);
            }
            function render(focusLayer = false) {
                if (!alive) return;
                if (!model.layers.some(layer => layer.id === selected)) selected = model.layers[0].id;
                clearGuides();
                const canvas = window.NotificationCard.render(board, example, { canvas: model, simulated: true });
                for (const box of canvas.children) {
                    const layer = model.layers.find(value => value.id === box.dataset.layer);
                    box.tabIndex = 0; box.setAttribute('role', 'group'); box.setAttribute('aria-roledescription', 'editable layer');
                    box.onkeydown = event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); select(layer.id); } else nudge(event, box); };
                    box.onpointerdown = event => beginDrag(event, box, box, false);
                    box.onclick = event => { if (!event.target.closest('.notificationStudioResize')) {select(layer.id);box.focus();} };
                }
                board.append(guideRoot);renderSelection(); renderInspector(); updateStatus();
                if (focusLayer) board.querySelector('[data-layer="' + selected + '"]')?.focus();
            }
            function keydown(event) {
                if (!alive || event.defaultPrevented || event.target.closest('input, textarea, select, [contenteditable]')) return;
                if (event.key === 'Escape' && cancelDrag) { event.preventDefault(); event.stopPropagation(); cancelDrag(); return; }
                if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
                const key = event.key.toLowerCase(), forward = key === 'y' || key === 'z' && event.shiftKey;
                if (key !== 'z' && key !== 'y') return; event.preventDefault(); event.stopPropagation(); cancelDrag?.();
                const from = forward ? redo : history, to = forward ? history : redo;
                if (!from.length) return; to.push(snapshot()); const state = from.pop(); model = state.model; selected = state.selected; commit(true);
            }
            board.onclick=event=>{if(event.target===board||event.target.classList.contains('notificationCanvas'))board.focus();};
            root.addEventListener('keydown', keydown); render();
            const dispose = () => { alive = false; cancelDrag?.(); root.removeEventListener('keydown', keydown); root.replaceChildren(); history = redo = []; };
            dispose.markSaved = value => { if (alive) { saved = copy(value || model); updateStatus(); } };
            dispose.getCanvas = () => alive ? copy(model) : null;
            dispose.validate = () => alive&&[...fields.querySelectorAll('input, select')].every(control=>control.reportValidity());
            return dispose;
        }
    };
})();
