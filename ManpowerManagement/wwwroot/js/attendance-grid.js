// Excel-like attendance grid: type hours or leave codes into cells, move with the keyboard, paste from Excel.
// Everyone counts the standard hours on a working day; only exceptions are keyed. Changes autosave to the server's staging file
// (no Submit button) and are copied to the database at 07:00 and 19:00.
(() => {
    const byId = id => document.getElementById(id);
    const grid = byId('agGrid');
    if (!grid) return;
    const data = JSON.parse(byId('agData').textContent);
    const STD = Number(data.standardHours), MAX_WEEK = Number(data.maxWeeklyHours), today = data.today, canEditPast = data.canEditPast, homeWorkshop = data.workshopId;
    const leaveCodes = Object.fromEntries(Object.entries(data.leaveCodes).map(([type, code]) => [Number(type), code]));
    const codeToType = Object.fromEntries(Object.entries(leaveCodes).map(([type, code]) => [code, Number(type)]));
    const codePattern = new RegExp(`^(${Object.values(leaveCodes).join('|')})(\\d+(?:\\.\\d+)?)?$`);
    const days = data.days;
    const weeks = days.length / 7;
    const round2 = n => Math.round(n * 100) / 100;
    // Weekly totals follow what is typed, even in a cell flagged red (e.g. hours on a future OFF day).
    const cellHours = c => c.err ? (/^\d+(\.\d+)?$/.test(c.raw) ? Math.min(round2(Number(c.raw)), STD) : 0) : c.h;

    const rows = data.rows.map(row => ({
        ...row,
        cells: row.cells.map(c => {
            const cell = { h: Number(c.h), lt: c.lt, lh: Number(c.lh), w: c.w || homeWorkshop, rs: !!c.rs, err: null, raw: '' };
            cell.orig = { h: cell.h, lt: cell.lt, lh: cell.lh, w: cell.w };
            return cell;
        })
    }));
    const INDIRECT_BANDS = ['10.67', '8.41', '7.33'];
    const sum = arr => arr.reduce((a, b) => a + b, 0);
    const manpower = days.map(d => { const ind = d.ind ? [...d.ind] : null; return { direct: d.direct, ind, orig: ind ? ind.join() : '' }; });
    // Attendance leaders are indirect; the daily hour summary deducts their confirmed hours.
    const leadersPresent = j => rows.filter(row => row.leader && !row.cells[j].err && row.cells[j].h > 0 && row.cells[j].w === homeWorkshop).length;
    const indirectAt = (m, j) => m.ind ? sum(m.ind) + leadersPresent(j) : null;

    // ---------- cell rules ----------
    const isFuture = j => days[j].date > today;
    const isLocked = j => !canEditPast && days[j].date < today;
    const isManpowerLocked = j => isFuture(j) || isLocked(j);
    const defaultValue = j => days[j].off ? { h: 0, lt: 0, lh: 0 } : { h: STD, lt: 0, lh: 0 };

    function parse(text, j) {
        const day = days[j], v = String(text ?? '').trim().toUpperCase().replace(/\s+/g, '');
        if (v === '' || v === '-') return defaultValue(j);
        if (v === 'OFF' || v === 'H') return day.off ? { h: 0, lt: 0, lh: 0 } : { err: 'This is not an OFF day for the shift.' };
        if (/^\d+(\.\d+)?$/.test(v)) {
            const hours = round2(Number(v));
            if (hours > STD) return { err: `Working hours cannot exceed ${STD}.` };
            if (isFuture(j) && hours !== defaultValue(j).h) return { err: 'Only leave can be entered ahead of today.' };
            return { h: hours, lt: 0, lh: 0 };
        }
        const match = v.match(codePattern);
        if (match) {
            if (day.off) return { err: 'OFF day: only OT working hours can be entered.' };
            const leaveHours = match[2] ? round2(Number(match[2])) : STD;
            if (leaveHours <= 0 || leaveHours > STD) return { err: `Leave hours must be more than 0 and at most ${STD}.` };
            return { h: round2(STD - leaveHours), lt: codeToType[match[1]], lh: leaveHours };
        }
        return { err: 'Type hours (e.g. 7.33) or a code: ' + Object.values(leaveCodes).join(' ') + ' (e.g. SL4).' };
    }

    function display(cell, j) {
        if (cell.rs) return 'Resigned';
        if (cell.err) return cell.raw;
        if (cell.lt) { const code = leaveCodes[cell.lt] || '?'; return cell.lh >= STD ? code : code + Number(cell.lh.toFixed(2)); }
        if (days[j].off && cell.h === 0) return 'OFF';
        return cell.h.toFixed(2);
    }

    const isDirty = cell => cell.err || cell.h !== cell.orig.h || cell.lt !== cell.orig.lt || cell.lh !== cell.orig.lh || cell.w !== cell.orig.w;

    // ---------- render ----------
    const cellEls = [], weekEls = [], presentEls = [], leaveEls = [], directEls = [], indirectEls = [];
    function fmtRange(a, b) {
        const s = new Date(a + 'T00:00'), e = new Date(b + 'T00:00'), m = d => d.toLocaleString('en', { month: 'short' });
        return s.getMonth() === e.getMonth() ? `${m(s)} ${s.getDate()} – ${e.getDate()}` : `${m(s)} ${s.getDate()} – ${m(e)} ${e.getDate()}`;
    }
    function dayClasses(j) {
        const d = days[j];
        return [d.off && 'off', !d.inMonth && 'out', d.date === today && 'today', isFuture(j) && 'future', ['Sat', 'Sun'].includes(d.dow) && 'weekend'].filter(Boolean).join(' ');
    }

    function render() {
        const head1 = ['<th class="ag-s ag-s0" rowspan="2">No.</th><th class="ag-s ag-s1" rowspan="2">Emp ID</th><th class="ag-s ag-s2" rowspan="2">Name</th><th class="ag-s ag-s3" rowspan="2">Process</th>'];
        const head2 = [];
        for (let w = 0; w < weeks; w++) {
            head1.push(`<th class="ag-week" colspan="8">${fmtRange(days[w * 7].date, days[w * 7 + 6].date)}</th>`);
            for (let j = w * 7; j < w * 7 + 7; j++) {
                const d = days[j], title = d.counted ? 'Counted in the daily hour summary' : '';
                head2.push(`<th class="ag-day ${dayClasses(j)}" data-j="${j}" title="${title}"><span>${d.day}</span><small>${d.off ? 'OFF' : d.dow}</small></th>`);
            }
            head2.push('<th class="ag-ttl-h">TTL</th>');
        }
        const body = rows.map((row, r) => {
            const tds = [`<td class="ag-s ag-s0">${r + 1}</td><td class="ag-s ag-s1">${esc(row.code)}</td><td class="ag-s ag-s2" title="${esc(row.name)}${row.leader ? ' · Attendance leader (Indirect, attendance only)' : ''}">${row.leader ? '<span class="ag-leader-tag">LDR</span> ' : ''}${esc(row.name)}</td><td class="ag-s ag-s3" title="${esc(row.process)}">${esc(row.process)}</td>`];
            for (let w = 0; w < weeks; w++) {
                for (let j = w * 7; j < w * 7 + 7; j++) tds.push(`<td class="ag-c" data-r="${r}" data-j="${j}"></td>`);
                tds.push(`<td class="ag-ttl" data-r="${r}" data-w="${w}"></td>`);
            }
            return `<tr data-r="${r}">${tds.join('')}</tr>`;
        });
        const foot = [['present', 'Present (auto)'], ['leave', 'On leave (auto)'], ['indirect', 'Indirect MP'], ['direct', 'Direct MP (auto)']].map(([key, label]) => {
            const tds = [`<th class="ag-s ag-foot-label" colspan="4">${label}${key === 'direct' ? ' <small>used for productivity</small>' : ''}</th>`];
            for (let w = 0; w < weeks; w++) {
                for (let j = w * 7; j < w * 7 + 7; j++) {
                    if (key === 'indirect') {
                        const locked = isManpowerLocked(j);
                        tds.push(`<td class="ag-f ag-mp-cell ${dayClasses(j)}"><button type="button" class="ag-mp ag-ind-btn" data-j="${j}" aria-label="${label} ${days[j].date}" ${locked ? 'disabled' : ''}></button></td>`);
                    } else if (key === 'direct') tds.push(`<td class="ag-f ag-mp-cell ag-direct ${dayClasses(j)}" data-j="${j}"></td>`);
                    else tds.push(`<td class="ag-f ${dayClasses(j)}" data-j="${j}"></td>`);
                }
                tds.push('<td class="ag-f ag-ttl"></td>');
            }
            return `<tr class="ag-foot-${key}">${tds.join('')}</tr>`;
        });
        grid.innerHTML = `<thead><tr>${head1.join('')}</tr><tr>${head2.join('')}</tr></thead><tbody>${body.join('')}</tbody><tfoot>${foot.join('')}</tfoot>`;

        grid.querySelectorAll('tbody tr').forEach((tr, r) => {
            cellEls[r] = [...tr.querySelectorAll('.ag-c')];
            weekEls[r] = [...tr.querySelectorAll('.ag-ttl')];
        });
        const footCells = key => [...grid.querySelectorAll(`.ag-foot-${key} td.ag-f:not(.ag-ttl)`)];
        presentEls.push(...footCells('present')); leaveEls.push(...footCells('leave'));
        directEls.push(...grid.querySelectorAll('.ag-direct')); indirectEls.push(...grid.querySelectorAll('.ag-ind-btn'));
        rows.forEach((_, r) => { days.forEach((_, j) => paintCell(r, j)); updateWeeks(r); });
        days.forEach((_, j) => updateColumn(j));
    }

    function esc(s) { return String(s ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]); }

    function paintCell(r, j) {
        const cell = rows[r].cells[j], el = cellEls[r][j], dirty = isDirty(cell);
        el.textContent = display(cell, j);
        el.className = ['ag-c', dayClasses(j),
            cell.lt && `lt-${cell.lt}`,
            !cell.err && !cell.lt && !days[j].off && cell.h !== STD && 'custom',
            days[j].off && cell.h > 0 && 'ot',
            cell.w !== homeWorkshop && 'transfer',
            cell.err && 'err',
            isLocked(j) && 'locked',
            cell.rs ? 'resigned' : dirty ? 'dirty' : 'ok'].filter(Boolean).join(' ');
        el.title = cell.rs ? 'Resigned: past this employee\'s last working day' : cell.err || (cell.w !== homeWorkshop ? `Worked in ${data.workshops[cell.w] || 'another workshop'}` : '');
        if (selected.has(el)) el.classList.add('sel');
        if (active.r === r && active.j === j) el.classList.add('active');
    }

    function updateWeeks(r) {
        for (let w = 0; w < weeks; w++) {
            if (rows[r].leader) { weekEls[r][w].textContent = '—'; weekEls[r][w].title = 'Attendance leader: hours are included as indirect in the daily summary'; continue; }
            const total = rows[r].cells.slice(w * 7, w * 7 + 7).reduce((sum, c) => sum + cellHours(c), 0);
            weekEls[r][w].textContent = total.toFixed(2);
            weekEls[r][w].classList.toggle('over', total > MAX_WEEK);
            weekEls[r][w].title = total > MAX_WEEK ? `Exceeds the ${MAX_WEEK} hour weekly limit` : '';
        }
    }

    function counts(j) {
        let present = 0, leave = 0;
        rows.forEach(row => { const c = row.cells[j]; if (c.err) return; if (c.h > 0 && c.w === homeWorkshop) present++; if (c.lt) leave++; });
        return { present, leave };
    }

    function updateColumn(j) {
        const { present, leave } = counts(j), m = manpower[j];
        presentEls[j].textContent = days[j].off && present === 0 ? '' : present;
        leaveEls[j].textContent = leave || '';
        const indirect = indirectAt(m, j);
        if (m.ind || !isManpowerLocked(j)) m.direct = indirect == null ? null : present - indirect;
        indirectEls[j].textContent = indirect ?? '';
        indirectEls[j].title = m.ind ? INDIRECT_BANDS.map((b, i) => `${b} h: ${m.ind[i]}`).concat(`Leaders: ${leadersPresent(j)}`).join(' · ') : 'Click to enter indirect manpower';
        directEls[j].textContent = m.direct ?? '';
        const mismatch = m.direct != null && m.direct < 0;
        [directEls[j], indirectEls[j].parentElement].forEach(el => { el.classList.toggle('mismatch', mismatch); el.title = mismatch ? `Indirect (${indirect}) is more than present (${present})` : el.title; });
        const missing = !isManpowerLocked(j) && !days[j].off && !m.ind;
        [directEls[j], indirectEls[j].parentElement].forEach(el => el.classList.toggle('missing', missing));
    }

    // ---------- autosave ----------
    const saveState = byId('agDirty'), token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    let saveTimer = null, saving = false, leaving = false;
    const manpowerDirty = m => (m.ind ? m.ind.join() : '') !== m.orig;
    const anyErrors = () => rows.some(row => row.cells.some(c => c.err));
    function anyDirty() { return rows.some(row => row.cells.some(isDirty)) || manpower.some(manpowerDirty); }
    function setSaveState(text, kind) { saveState.hidden = !text; saveState.textContent = text || ''; saveState.style.color = kind === 'error' ? '#c0392b' : kind === 'saved' ? '#1c7a4b' : ''; }
    function refreshDirty() { if (anyDirty()) { setSaveState('● Saving…'); clearTimeout(saveTimer); saveTimer = setTimeout(saveNow, 600); } }
    function stageUrl() {
        const url = new URL(location.pathname, location.origin);
        url.searchParams.set('handler', 'Stage'); url.searchParams.set('month', data.month);
        url.searchParams.set('workshopId', homeWorkshop); url.searchParams.set('shift', data.shift);
        return url;
    }
    async function saveNow() {
        saveTimer = null;
        if (saving) { saveTimer = setTimeout(saveNow, 300); return; }
        const sentCells = [], sentManpower = [];
        rows.forEach((row, r) => row.cells.forEach((c, j) => { if (!c.err && !c.rs && isDirty(c)) sentCells.push({ r, j, h: c.h, lt: c.lt, lh: c.lh, w: c.w }); }));
        manpower.forEach((m, j) => { if (m.ind && manpowerDirty(m)) sentManpower.push({ j, ind: [...m.ind] }); });
        if (!sentCells.length && !sentManpower.length) { if (anyErrors()) setSaveState('⚠ Fix the red cells to save them', 'error'); else setSaveState('✓ Saved', 'saved'); return; }
        saving = true;
        const body = {
            cells: sentCells.map(c => ({ employeeId: rows[c.r].id, date: days[c.j].date, hours: c.h, leaveType: c.lt, leaveHours: c.lh, workshopId: c.w })),
            manpower: sentManpower.map(m => ({ date: days[m.j].date, i1067: m.ind[0], i841: m.ind[1], i733: m.ind[2] }))
        };
        try {
            const response = await fetch(stageUrl(), { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token || '' }, body: JSON.stringify(body), credentials: 'same-origin' });
            if (response.status === 401) { setSaveState('⚠ Not saved: check in again, then reload.', 'error'); return; }
            if (!response.ok) throw new Error(String(response.status));
            const result = await response.json();
            if (!result.ok) { setSaveState('⚠ ' + (result.error || 'Not saved'), 'error'); return; }
            const failed = new Map((result.failures || []).map(f => [`${f.employeeId}|${f.date}`, f.message]));
            const touched = new Set();
            sentCells.forEach(c => {
                const cell = rows[c.r].cells[c.j], message = failed.get(`${rows[c.r].id}|${days[c.j].date}`);
                if (message) { cell.raw = display(cell, c.j); cell.err = message; }
                else cell.orig = { h: c.h, lt: c.lt, lh: c.lh, w: c.w };
                paintCell(c.r, c.j); touched.add(c.r);
            });
            sentManpower.forEach(m => { if (!failed.has(`0|${days[m.j].date}`)) manpower[m.j].orig = m.ind.join(); });
            touched.forEach(updateWeeks); showStatus();
            const failures = result.failures || [];
            if (failures.length) { setSaveState(`⚠ ${failures.length} change${failures.length === 1 ? '' : 's'} not saved: ${failures[0].message}`, 'error'); showStatus(failures[0].message, true); }
            else setSaveState('✓ Saved', 'saved');
        } catch {
            setSaveState('⚠ Could not reach the server. Retrying…', 'error');
            clearTimeout(saveTimer); saveTimer = setTimeout(saveNow, 5000);
        } finally {
            saving = false;
            // Edits made while the request was in flight still need saving.
            if (!saveTimer && (rows.some(row => row.cells.some(c => !c.err && isDirty(c))) || manpower.some(manpowerDirty))) saveTimer = setTimeout(saveNow, 600);
        }
    }
    window.addEventListener('beforeunload', e => { if (!leaving && (saving || rows.some(row => row.cells.some(c => isDirty(c) && !c.err)) || manpower.some(manpowerDirty))) { e.preventDefault(); e.returnValue = ''; } });
    document.querySelector('[data-nav-select]')?.form.addEventListener('submit', e => { if (anyDirty() && !confirm('Some changes have not finished saving. Switch shift anyway?')) { e.preventDefault(); location.reload(); } else leaving = true; });
    document.querySelectorAll('[data-nav]').forEach(a => a.addEventListener('click', e => { if (anyDirty() && !confirm('Some changes have not finished saving. Leave this month anyway?')) e.preventDefault(); else leaving = true; }));

    // ---------- selection ----------
    const wrap = byId('agWrap'), editor = byId('agEditor'), status = byId('agStatus');
    let anchor = { r: 0, j: 0 }, active = { r: 0, j: 0 }, dragging = false, editing = false, editByTyping = false;
    const selected = new Set();
    let visible = rows.map((_, r) => r);

    function rect() { return { r0: Math.min(anchor.r, active.r), r1: Math.max(anchor.r, active.r), j0: Math.min(anchor.j, active.j), j1: Math.max(anchor.j, active.j) }; }
    function selectedCells() {
        const { r0, r1, j0, j1 } = rect(), out = [];
        for (const r of visible) if (r >= r0 && r <= r1) for (let j = j0; j <= j1; j++) out.push({ r, j });
        return out;
    }
    function paintSelection() {
        grid.querySelectorAll('td.ag-c.sel, td.ag-c.active').forEach(el => el.classList.remove('sel', 'active'));
        selected.clear();
        selectedCells().forEach(({ r, j }) => { const el = cellEls[r][j]; el.classList.add('sel'); selected.add(el); });
        const activeEl = cellEls[active.r]?.[active.j];
        if (activeEl) { activeEl.classList.add('active'); selected.add(activeEl); }
        grid.querySelectorAll('.ag-day.col-active').forEach(th => th.classList.remove('col-active'));
        grid.querySelector(`.ag-day[data-j="${active.j}"]`)?.classList.add('col-active');
        showStatus();
    }
    function showStatus(message, isError) {
        if (message) { status.textContent = message; status.className = 'ag-status' + (isError ? ' error' : ' info'); return; }
        const row = rows[active.r]; if (!row) return;
        const cell = row.cells[active.j], d = days[active.j], n = selectedCells().length;
        const what = cell.rs ? 'Resigned (past last working day)' : cell.err ? `⚠ ${cell.err}` : cell.lt ? `${leaveName(cell.lt)} ${cell.lh.toFixed(2)} hrs${cell.h ? ` · worked ${cell.h.toFixed(2)} hrs` : ''}` : d.off && cell.h === 0 ? 'OFF day' : `${cell.h.toFixed(2)} hrs${d.off ? ' OT' : ''}`;
        status.textContent = `${row.name} · ${d.dow} ${d.date} · ${what}${cell.w !== homeWorkshop ? ` · transfer to ${data.workshops[cell.w] || '?'}` : ''}${isLocked(active.j) ? ' · locked (previous date, Admin only)' : ''}${n > 1 ? ` · ${n} cells selected` : ''}`;
        status.className = 'ag-status' + (cell.err ? ' error' : '');
    }
    const leaveNames = { AL: 'Annual leave', SL: 'Sick leave', BL: 'Business leave', OL: 'Other leave', AB: 'Absent', ML: 'Mother leave' };
    const leaveName = lt => leaveNames[leaveCodes[lt]] || 'Leave';

    function setActive(r, j, extend) {
        active = { r, j };
        if (!extend) anchor = { r, j };
        paintSelection(); scrollIntoView(cellEls[r][j]);
    }
    function move(dr, dj, extend) {
        const pos = Math.max(0, visible.indexOf(active.r));
        const r = visible[Math.min(visible.length - 1, Math.max(0, pos + dr))];
        const j = Math.min(days.length - 1, Math.max(0, active.j + dj));
        if (r !== undefined) setActive(r, j, extend);
    }
    function scrollIntoView(el) {
        if (!el) return;
        const w = wrap.getBoundingClientRect(), c = el.getBoundingClientRect();
        const stickyLeft = Math.max(0, ...[...grid.querySelectorAll('tbody tr:not([hidden]) .ag-s')].slice(0, 4).map(s => s.getBoundingClientRect().right - w.left));
        const headBottom = grid.querySelector('thead').getBoundingClientRect().bottom - w.top;
        const footHeight = grid.querySelector('tfoot').getBoundingClientRect().height;
        if (c.left - w.left < stickyLeft) wrap.scrollLeft -= stickyLeft - (c.left - w.left);
        else if (c.right > w.right - 14) wrap.scrollLeft += c.right - w.right + 14;
        if (c.top - w.top < headBottom) wrap.scrollTop -= headBottom - (c.top - w.top);
        else if (c.bottom > w.bottom - footHeight - 14) wrap.scrollTop += c.bottom - (w.bottom - footHeight - 14);
    }

    // ---------- editing ----------
    function setValue(r, j, text) {
        if (isLocked(j) || rows[r].cells[j].rs) return false;
        const cell = rows[r].cells[j], parsed = parse(text, j);
        if (parsed.err) { cell.err = parsed.err; cell.raw = String(text).trim(); }
        else { cell.err = null; cell.raw = ''; cell.h = parsed.h; cell.lt = parsed.lt; cell.lh = parsed.lh; }
        paintCell(r, j);
        return true;
    }
    function afterChange(targets) {
        new Set(targets.map(t => t.r)).forEach(updateWeeks);
        new Set(targets.map(t => t.j)).forEach(updateColumn);
        refreshDirty(); showStatus();
    }
    function applyToCells(targets, text) {
        let skipped = 0; const changed = [];
        targets.forEach(t => { if (setValue(t.r, t.j, text)) changed.push(t); else skipped++; });
        afterChange(changed);
        if (skipped) showStatus(`${skipped} locked cell${skipped === 1 ? ' was' : 's were'} skipped (previous dates can be changed by Admin only).`, false);
    }

    function startEdit(initial, byTyping) {
        if (isLocked(active.j)) { showStatus('Previous dates are locked. Ask Admin to correct them.', true); return; }
        if (rows[active.r].cells[active.j].rs) { showStatus('This employee has resigned: nothing can be entered after the last working day.', true); return; }
        const el = cellEls[active.r][active.j];
        scrollIntoView(el);
        const w = wrap.getBoundingClientRect(), c = el.getBoundingClientRect();
        Object.assign(editor.style, { left: `${c.left - w.left + wrap.scrollLeft}px`, top: `${c.top - w.top + wrap.scrollTop}px`, width: `${Math.max(c.width, 80)}px`, height: `${c.height}px` });
        editor.value = byTyping ? initial : display(rows[active.r].cells[active.j], active.j);
        editor.hidden = false; editing = true; editByTyping = byTyping;
        editor.focus();
        if (!byTyping) editor.select();
    }
    function commitEdit(fillSelection) {
        if (!editing) return;
        editing = false; editor.hidden = true;
        applyToCells(fillSelection ? selectedCells() : [active], editor.value);
        wrap.focus({ preventScroll: true });
    }
    function cancelEdit() { editing = false; editor.hidden = true; wrap.focus({ preventScroll: true }); }

    editor.addEventListener('keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); commitEdit(e.ctrlKey || e.metaKey); if (!e.ctrlKey && !e.metaKey) move(e.shiftKey ? -1 : 1, 0); }
        else if (e.key === 'Tab') { e.preventDefault(); commitEdit(false); move(0, e.shiftKey ? -1 : 1); }
        else if (e.key === 'Escape') { e.preventDefault(); cancelEdit(); }
        else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') { e.preventDefault(); commitEdit(false); move(e.key === 'ArrowUp' ? -1 : 1, 0); }
        else if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && editByTyping) { e.preventDefault(); commitEdit(false); move(0, e.key === 'ArrowLeft' ? -1 : 1); }
    });
    editor.addEventListener('blur', () => commitEdit(false));

    wrap.addEventListener('keydown', e => {
        if (editing || e.target !== wrap) return;
        const ctrl = e.ctrlKey || e.metaKey;
        const arrows = { ArrowUp: [-1, 0], ArrowDown: [1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1] };
        if (arrows[e.key]) { e.preventDefault(); move(...arrows[e.key], e.shiftKey); }
        else if (e.key === 'Tab') { e.preventDefault(); move(0, e.shiftKey ? -1 : 1); }
        else if (e.key === 'Enter') { e.preventDefault(); move(e.shiftKey ? -1 : 1, 0); }
        else if (e.key === 'F2') { e.preventDefault(); startEdit('', false); }
        else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); applyToCells(selectedCells(), ''); }
        else if (e.key === 'Escape') { anchor = { ...active }; paintSelection(); }
        else if (ctrl && e.key.toLowerCase() === 'a') { e.preventDefault(); anchor = { r: visible[0], j: 0 }; active = { r: visible[visible.length - 1], j: days.length - 1 }; paintSelection(); }
        else if (e.key.length === 1 && !ctrl && !e.altKey) { e.preventDefault(); startEdit(e.key, true); }
    });

    grid.addEventListener('mousedown', e => {
        const td = e.target.closest('td.ag-c'); if (!td) return;
        e.preventDefault();
        if (editing) commitEdit(false);
        const r = Number(td.dataset.r), j = Number(td.dataset.j);
        setActive(r, j, e.shiftKey); dragging = true;
        wrap.focus({ preventScroll: true });
    });
    grid.addEventListener('mouseover', e => {
        if (!dragging) return;
        const td = e.target.closest('td.ag-c'); if (!td) return;
        active = { r: Number(td.dataset.r), j: Number(td.dataset.j) }; paintSelection();
    });
    document.addEventListener('mouseup', () => { dragging = false; });
    grid.addEventListener('dblclick', e => { if (e.target.closest('td.ag-c')) startEdit('', false); });

    // ---------- clipboard (Excel TSV) ----------
    document.addEventListener('copy', e => {
        if (editing || document.activeElement !== wrap) return;
        const { r0, r1, j0, j1 } = rect(), lines = [];
        for (const r of visible) if (r >= r0 && r <= r1) { const vals = []; for (let j = j0; j <= j1; j++) { vals.push(display(rows[r].cells[j], j)); if (days[j].dow === 'Sat' && j < j1) vals.push(weekEls[r][Math.floor(j / 7)].textContent); } lines.push(vals.join('\t')); }
        e.clipboardData.setData('text/plain', lines.join('\r\n')); e.preventDefault();
        showStatus(`Copied ${lines.length} × ${j1 - j0 + 1} cells.`);
    });
    document.addEventListener('paste', e => {
        if (editing || document.activeElement !== wrap) return;
        e.preventDefault();
        const text = (e.clipboardData.getData('text/plain') || '').replace(/\r?\n$/, '');
        const matrix = text.split(/\r?\n/).map(line => line.split('\t'));
        if (!matrix.length) return;
        // One copied value pasted onto a selected range fills the whole range, as in Excel.
        if (matrix.length === 1 && matrix[0].length === 1) { applyToCells(selectedCells(), matrix[0][0]); return; }
        const { r0, j0 } = rect(), start = visible.indexOf(r0), targets = [];
        let skipped = 0;
        matrix.forEach((values, i) => {
            const r = visible[start + i]; if (r === undefined) return;
            // The Excel sheets have a weekly total column after each Saturday, just like this grid; skip it.
            let j = j0, k = 0;
            while (k < values.length && j < days.length) {
                if (setValue(r, j, values[k])) targets.push({ r, j }); else skipped++;
                k++;
                if (days[j].dow === 'Sat' && k < values.length) k++;
                j++;
            }
        });
        afterChange(targets);
        const errors = targets.filter(t => rows[t.r].cells[t.j].err).length;
        showStatus(`Pasted ${targets.length} cells${skipped ? `, ${skipped} locked skipped` : ''}${errors ? ` · ${errors} need fixing (red)` : ''}.`, errors > 0);
    });

    // ---------- toolbar ----------
    document.querySelectorAll('[data-fill]').forEach(btn => btn.addEventListener('click', () => { applyToCells(selectedCells(), btn.dataset.fill); wrap.focus({ preventScroll: true }); }));
    byId('agSearch')?.addEventListener('input', e => {
        const q = e.target.value.trim().toLowerCase();
        visible = [];
        grid.querySelectorAll('tbody tr').forEach((tr, r) => { const show = !q || `${rows[r].code} ${rows[r].name} ${rows[r].process}`.toLowerCase().includes(q); tr.hidden = !show; if (show) visible.push(r); });
        if (visible.length && !visible.includes(active.r)) setActive(visible[0], active.j); else paintSelection();
    });
    const todayIndex = days.findIndex(d => d.date === today);
    byId('agToday')?.addEventListener('click', () => {
        if (todayIndex < 0) { const url = new URL(location.href); url.searchParams.set('month', today.slice(0, 7)); location.href = url; return; }
        setActive(visible[0] ?? 0, todayIndex); wrap.focus({ preventScroll: true });
    });
    const transferModal = byId('agTransferModal');
    byId('agTransfer')?.addEventListener('click', () => {
        const n = selectedCells().filter(t => !isLocked(t.j) && !rows[t.r].cells[t.j].rs).length;
        byId('agTransferContext').textContent = `${n} selected cell${n === 1 ? '' : 's'}`;
        byId('agTransferWorkshop').value = String(rows[active.r]?.cells[active.j].w ?? homeWorkshop);
        bootstrap.Modal.getOrCreateInstance(transferModal).show();
    });
    byId('agTransferApply')?.addEventListener('click', () => {
        const workshop = Number(byId('agTransferWorkshop').value), targets = selectedCells().filter(t => !isLocked(t.j) && !rows[t.r].cells[t.j].rs);
        targets.forEach(t => { rows[t.r].cells[t.j].w = workshop; paintCell(t.r, t.j); });
        afterChange(targets);
        bootstrap.Modal.getOrCreateInstance(transferModal).hide();
    });
    transferModal?.addEventListener('hidden.bs.modal', () => wrap.focus({ preventScroll: true }));

    // ---------- Direct / Indirect ----------
    const indirectModal = byId('agIndirectModal'), indInputs = [...document.querySelectorAll('.ag-ind')];
    let indirectDay = -1;
    function renderIndirectCalc() {
        const vals = indInputs.map(i => i.value === '' ? 0 : Number(i.value)), leaders = leadersPresent(indirectDay), total = sum(vals) + leaders, { present } = counts(indirectDay);
        byId('agIndirectCalc').innerHTML = `${leaders ? `Leaders (auto): <strong>${leaders}</strong> · ` : ''}Indirect total: <strong>${total}</strong> · Present: <strong>${present}</strong> · Direct: <strong>${present - total}</strong>`;
        const err = byId('agIndirectError'); err.hidden = total <= present; err.textContent = `Indirect (${total}) cannot be more than present (${present}).`;
        byId('agIndirectApply').disabled = total > present;
    }
    grid.addEventListener('click', e => {
        const btn = e.target.closest('.ag-ind-btn'); if (!btn || btn.disabled) return;
        indirectDay = Number(btn.dataset.j);
        const m = manpower[indirectDay], d = days[indirectDay];
        byId('agIndirectContext').textContent = `${d.dow} ${d.date}`;
        indInputs.forEach((input, i) => input.value = m.ind ? m.ind[i] : '');
        renderIndirectCalc();
        bootstrap.Modal.getOrCreateInstance(indirectModal).show();
    });
    indInputs.forEach((input, i) => {
        input.addEventListener('input', () => { input.value = input.value.replace(/\D/g, ''); renderIndirectCalc(); });
        input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); (indInputs[i + 1] ?? byId('agIndirectApply')).focus(); } });
    });
    indirectModal?.addEventListener('shown.bs.modal', () => { indInputs[0].focus(); indInputs[0].select(); });
    indirectModal?.addEventListener('hidden.bs.modal', () => wrap.focus({ preventScroll: true }));
    byId('agIndirectApply')?.addEventListener('click', () => {
        const j = indirectDay; if (j < 0) return;
        manpower[j].ind = indInputs.map(i => i.value === '' ? 0 : Number(i.value));
        updateColumn(j); refreshDirty();
        bootstrap.Modal.getOrCreateInstance(indirectModal).hide();
    });

    render();
    setActive(visible[0] ?? 0, todayIndex >= 0 ? todayIndex : 0);
    requestAnimationFrame(() => scrollIntoView(cellEls[active.r]?.[active.j]));

})();
