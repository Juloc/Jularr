(() => {
    const editor = document.querySelector('[data-rre-editor]');
    if (!editor) return;
    const workspace = editor.closest('[data-rre-workspace]');
    const form = editor.querySelector('[data-rre-form]');
    const profiles = JSON.parse(editor.querySelector('[data-rre-profiles]').textContent);
    const originalFlags = JSON.parse(editor.querySelector('[data-rre-custom-fields]').textContent);
    const userInitial = editor.querySelector('[data-rre-initial]');
    const initialUser = userInitial ? JSON.parse(userInitial.textContent) : null;
    const newValues = JSON.parse(editor.querySelector('[data-rre-new-values]').textContent);
    const isUser = editor.dataset.user === 'true';
    const field = name => form.elements.namedItem(`Editor.${name}`);
    const id = field('Id');
    const assignment = field('RuleId');
    const overrides = form.querySelector('[data-rre-overrides]');
    const unlimited = form.querySelector('[data-rre-unlimited]');
    const limit = form.querySelector('[data-rre-limit]');
    const period = form.querySelector('[data-rre-period]');
    const approval = form.querySelector('[data-rre-approval]');
    const kinds = [...form.querySelectorAll('[data-rre-kind]')];
    const qualityProfiles = [...form.querySelectorAll('[data-rre-quality]')];
    const fields = form.querySelector('[data-rre-fields]');
    const initialId = id.value;
    let base = profiles.find(profile => profile.id === (isUser ? assignment.value || editor.dataset.defaultId : id.value));
    let customFields = { ...originalFlags };
    let retainedKinds = (initialUser?.kinds || base?.values.kinds || []).filter(kind => !kinds.some(input => input.value === kind));
    let retainedQuality = (initialUser?.qualityProfileIds || base?.values.qualityProfileIds || []).filter(id => !qualityProfiles.some(input => input.value === id));
    let dirty = false;
    let rendering = false;
    let leaving = false;
    const equal = (left, right) => Array.isArray(left) ? [...left].sort().join('\0') === [...right].sort().join('\0') : left === right;
    const readValues = () => ({
        limit: unlimited.checked ? null : Number(limit.value),
        periodDays: Number(period.value),
        approval: approval.value,
        kinds: [...kinds.filter(input => input.checked).map(input => input.value), ...retainedKinds],
        qualityProfileIds: [...qualityProfiles.filter(input => input.checked).map(input => input.value), ...retainedQuality]
    });
    const indicate = () => {
        if (!isUser) return;
        fields.disabled = !overrides.checked;
        editor.querySelector('[data-rre-state]').textContent = editor.dataset.based.replace('{name}', base.name);
        for (const indicator of form.querySelectorAll('[data-rre-inheritance]')) {
            indicator.textContent = overrides.checked && customFields[indicator.dataset.rreInheritance] ? editor.dataset.custom : editor.dataset.inherited;
        }
    };
    const fillValues = values => {
        rendering = true;
        unlimited.checked = values.limit === null;
        limit.value = String(values.limit ?? newValues.limit ?? 10);
        limit.disabled = unlimited.checked;
        period.value = String(values.periodDays);
        approval.value = values.approval;
        approval.dispatchEvent(new Event('change', { bubbles: true }));
        retainedKinds = values.kinds.filter(kind => !kinds.some(input => input.value === kind));
        for (const input of kinds) input.checked = values.kinds.includes(input.value);
        retainedQuality = (values.qualityProfileIds || []).filter(id => !qualityProfiles.some(input => input.value === id));
        for (const input of qualityProfiles) input.checked = (values.qualityProfileIds || []).includes(input.value);
        rendering = false;
        indicate();
    };
    const allowDiscard = () => !dirty || window.confirm(editor.dataset.unsaved);
    const showEditor = () => {
        workspace.classList.add('rre-mobile-editor');
        (form.querySelector('[data-rre-name]') || editor.querySelector('[data-admreq-select-trigger]') || assignment)?.focus();
    };
    const selectProfile = profile => {
        base = profile;
        id.value = profile?.id || '';
        field('Name').value = profile?.name || '';
        field('Description').value = profile?.description || '';
        fillValues(profile?.values || newValues);
        field('Revision').value = editor.dataset.revision;
        for (const row of workspace.querySelectorAll('[data-rre-profile]')) {
            const selected = row.dataset.rreProfile === profile?.id;
            row.classList.toggle('is-selected', selected);
            row.querySelector('[data-rre-select]').setAttribute('aria-pressed', String(selected));
        }
        dirty = false;
        showEditor();
    };
    for (const button of workspace.querySelectorAll('[data-rre-select]')) {
        button.addEventListener('click', event => {
            event.preventDefault();
            if (allowDiscard()) selectProfile(profiles.find(profile => profile.id === button.dataset.rreSelect));
        });
    }
    workspace.querySelector('[data-rre-new]')?.addEventListener('click', () => { if (allowDiscard()) selectProfile(null); });
    form.addEventListener('input', event => {
        if (rendering) return;
        dirty = true;
        if (event.target === unlimited) limit.disabled = unlimited.checked;
        if (isUser && overrides.checked) {
            const values = readValues();
            for (const key of Object.keys(customFields)) customFields[key] = !equal(values[key], base.values[key]);
        }
        indicate();
    });
    approval.addEventListener('change', () => {
        if (rendering) return;
        dirty = true;
        customFields.approval = approval.value !== base?.values.approval;
        indicate();
    });
    assignment?.addEventListener('change', () => {
        if (rendering) return;
        const edited = readValues();
        base = profiles.find(profile => profile.id === (assignment.value || editor.dataset.defaultId));
        const effective = { ...base.values };
        if (overrides.checked) {
            for (const key of Object.keys(customFields)) {
                if (customFields[key]) effective[key] = edited[key];
            }
        }
        field('Name').value = base.name;
        fillValues(effective);
        dirty = true;
    });
    overrides?.addEventListener('change', () => {
        if (!overrides.checked) {
            customFields = { limit: false, periodDays: false, approval: false, kinds: false, qualityProfileIds: false };
            fillValues(base.values);
        }
        indicate();
        dirty = true;
    });
    const reset = () => {
        if (isUser) {
            rendering = true;
            assignment.value = initialUser.ruleId;
            assignment.dispatchEvent(new Event('change', { bubbles: true }));
            rendering = false;
            base = profiles.find(profile => profile.id === (assignment.value || editor.dataset.defaultId));
            field('Name').value = base.name;
            overrides.checked = Object.values(originalFlags).some(Boolean);
            customFields = { ...originalFlags };
            fillValues(initialUser);
            field('Revision').value = editor.dataset.revision;
            dirty = false;
        }
        else selectProfile(profiles.find(profile => profile.id === (id.value || initialId || editor.dataset.defaultId)));
    };
    editor.querySelector('[data-rre-cancel]').addEventListener('click', reset);
    editor.querySelector('[data-rre-back]').addEventListener('click', () => {
        if (!allowDiscard()) return;
        reset();
        if (workspace.querySelector('.rre-list')) {
            workspace.classList.remove('rre-mobile-editor');
            (workspace.querySelector('[data-rre-new]') || workspace.querySelector('[data-rre-navigation]'))?.focus();
        }
        else {
            leaving = true;
            window.location.assign(document.querySelector('.rre-user-context a').href);
        }
    });
    form.addEventListener('submit', () => { leaving = true; });
    document.querySelectorAll('[data-rre-mutation], [data-rre-delete]').forEach(mutation => {
        mutation.addEventListener('submit', event => {
            if (!allowDiscard() || mutation.hasAttribute('data-rre-delete') && !window.confirm(mutation.dataset.confirm)) event.preventDefault();
            else leaving = true;
        });
    });
    document.querySelectorAll('[data-rre-navigation]').forEach(navigation => {
        navigation.addEventListener(navigation.tagName === 'FORM' ? 'submit' : 'click', event => {
            if (!allowDiscard()) event.preventDefault();
            else leaving = true;
        });
    });
    window.addEventListener('beforeunload', event => {
        if (dirty && !leaving) { event.preventDefault(); event.returnValue = ''; }
    });
    if (isUser && overrides.checked) {
        const values = readValues();
        for (const key of Object.keys(customFields)) customFields[key] = !equal(values[key], base.values[key]);
    }
    indicate();
})();
