(() => {
    const picker = document.getElementById('dashboard-workshop-picker');
    if (!picker) return;

    const nodes = JSON.parse(document.getElementById('dashboard-workshop-data').textContent);
    const byId = new Map(nodes.map(node => [node.id, node]));
    const children = new Map();
    for (const node of nodes) {
        const parentId = byId.has(node.parentId) ? node.parentId : null;
        if (!children.has(parentId)) children.set(parentId, []);
        children.get(parentId).push(node);
    }
    for (const siblings of children.values()) siblings.sort((a, b) => a.name.localeCompare(b.name));

    const trigger = document.getElementById('dashboard-workshop');
    const popover = document.getElementById('dashboard-workshop-popover');
    const search = document.getElementById('dashboard-workshop-search');
    const level = document.getElementById('dashboard-workshop-level');
    const results = document.getElementById('dashboard-workshop-results');
    const input = document.getElementById('dashboard-workshop-id');
    const value = document.getElementById('dashboard-workshop-value');
    let selectedId = Number(input.value) || null;
    let currentId = null;

    function ancestors(node) {
        const path = [];
        const seen = new Set([node.id]);
        let parentId = node.parentId;
        while (byId.has(parentId) && !seen.has(parentId)) {
            const parent = byId.get(parentId);
            path.unshift(parent);
            seen.add(parentId);
            parentId = parent.parentId;
        }
        return path;
    }

    function makeButton(label, className, onClick) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = className;
        button.textContent = label;
        button.addEventListener('click', onClick);
        return button;
    }

    function select(node) {
        selectedId = node?.id ?? null;
        input.value = selectedId ?? '';
        value.textContent = node?.name ?? 'All workshops';
        close();
        trigger.focus();
    }

    function open() {
        const selected = byId.get(selectedId);
        currentId = selected
            ? (children.has(selected.id) ? selected.id : (byId.has(selected.parentId) ? selected.parentId : null))
            : null;
        search.value = '';
        popover.hidden = false;
        trigger.setAttribute('aria-expanded', 'true');
        render();
        search.focus();
    }

    function close() {
        popover.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');
    }

    function addRow(node, context) {
        const row = document.createElement('div');
        row.className = 'wd-workshop-row' + (node?.id === selectedId ? ' is-selected' : '');
        const name = makeButton(node?.name ?? 'All workshops', 'wd-workshop-choice', () => select(node));
        if (context) {
            const detail = document.createElement('small');
            detail.textContent = context;
            name.append(detail);
        }
        if (node?.id === selectedId || (!node && selectedId === null)) {
            name.setAttribute('aria-current', 'true');
            const check = document.createElement('span');
            check.className = 'wd-workshop-check';
            check.textContent = '✓';
            check.setAttribute('aria-hidden', 'true');
            name.append(check);
        }
        row.append(name);
        if (node && children.has(node.id) && !context?.startsWith('View all in ')) {
            row.append(makeButton('›', 'wd-workshop-drill', () => {
                currentId = node.id;
                search.value = '';
                render();
                level.querySelector('button')?.focus();
            }));
            row.lastChild.setAttribute('aria-label', `Open ${node.name} subgroups`);
        }
        results.append(row);
    }

    function render() {
        level.replaceChildren();
        results.replaceChildren();
        const query = search.value.trim().toLocaleLowerCase();
        if (query) {
            level.textContent = 'Search results';
            const matches = nodes.filter(node =>
                [node.name, ...ancestors(node).map(parent => parent.name)]
                    .some(name => name.toLocaleLowerCase().includes(query)));
            for (const node of matches.slice(0, 100)) {
                addRow(node, ancestors(node).map(parent => parent.name).join(' › '));
            }
            if (!matches.length) results.textContent = 'No workshops found.';
            if (matches.length > 100) {
                const note = document.createElement('p');
                note.className = 'wd-workshop-hint';
                note.textContent = `Showing 100 of ${matches.length} matches. Refine your search.`;
                results.append(note);
            }
            return;
        }

        if (currentId === null) {
            level.textContent = 'All workshop groups';
            addRow(null);
        } else {
            const current = byId.get(currentId);
            const back = makeButton(`‹  ${current.name}`, 'wd-workshop-back', () => {
                currentId = byId.has(current.parentId) ? current.parentId : null;
                render();
                level.querySelector('button')?.focus();
            });
            level.append(back);
            addRow(current, `View all in ${current.name}`);
        }
        for (const node of children.get(currentId) ?? []) addRow(node);
    }

    trigger.addEventListener('click', () => popover.hidden ? open() : close());
    search.addEventListener('input', render);
    search.addEventListener('keydown', event => {
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            results.querySelector('button')?.focus();
        } else if (event.key === 'Enter') {
            event.preventDefault();
            results.querySelector('.wd-workshop-choice')?.click();
        }
    });
    popover.addEventListener('keydown', event => {
        if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
        const buttons = [...popover.querySelectorAll('button')];
        const index = buttons.indexOf(document.activeElement);
        if (index < 0) return;
        event.preventDefault();
        buttons[Math.max(0, Math.min(buttons.length - 1, index + (event.key === 'ArrowDown' ? 1 : -1)))].focus();
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !popover.hidden) {
            close();
            trigger.focus();
        }
    });
    document.addEventListener('pointerdown', event => {
        if (!popover.hidden && !picker.contains(event.target)) close();
    });
})();
