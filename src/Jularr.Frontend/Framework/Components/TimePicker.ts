// Wall-clock fields: a desktop types hours and minutes, Android turns a wheel, iOS keeps the system's own scrolling picker.
// The native time input stays the submitted value; without script it is the picker.
export type TimePlatform = 'desktop' | 'android' | 'ios';

export function timePlatform(agent: string, touchPoints: number): TimePlatform {
    if (/iPad|iPhone|iPod/.test(agent) || /Macintosh/.test(agent) && touchPoints > 1) return 'ios';
    return /Android/.test(agent) ? 'android' : 'desktop';
}

export function parseTime(value: string): [number, number] | null {
    const match = /^(\d{2}):(\d{2})/.exec(value);
    if (!match) return null;
    const hour = Number(match[1]), minute = Number(match[2]);
    return hour < 24 && minute < 60 ? [hour, minute] : null;
}

export const formatTime = (hour: number, minute: number) => `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;

// Only digits, at most two; a value past the maximum keeps its last digit.
export function cleanPart(text: string, max: number): string {
    const digits = text.replace(/\D/g, '').slice(-2);
    return Number(digits) > max ? digits.slice(-1) : digits;
}

export function initializeTimePickers(root: ParentNode = document): void {
    const platform = timePlatform(navigator.userAgent, navigator.maxTouchPoints);
    if (platform === 'ios') return;
    root.querySelectorAll<HTMLInputElement>('input[type="time"][data-time-picker]').forEach(input => {
        if (input.dataset.timeReady) return;
        input.dataset.timeReady = 'true';
        if (platform === 'android') wheel(input);
        else fields(input);
    });
}

function commit(input: HTMLInputElement, value: string): void {
    if (input.value === value) return;
    input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
}

// The visible control takes the input's id, so its label names it.
function replace(input: HTMLInputElement, control: HTMLElement, focusable: HTMLElement): void {
    focusable.id = input.id;
    input.removeAttribute('id');
    input.hidden = true;
    input.after(control);
}

function fields(input: HTMLInputElement): void {
    const [hour, minute] = parseTime(input.value) ?? [null, null];
    const part = (value: number | null, max: number, label: string) => {
        const field = document.createElement('input');
        field.type = 'text';
        field.inputMode = 'numeric';
        field.autocomplete = 'off';
        field.maxLength = 2;
        field.className = 'ui-time__part';
        field.required = input.required;
        field.value = value === null ? '' : String(value).padStart(2, '0');
        field.setAttribute('aria-label', label);
        field.dataset.max = String(max);
        return field;
    };
    const hours = part(hour, 23, input.dataset.timeHour ?? '');
    const minutes = part(minute, 59, input.dataset.timeMinute ?? '');
    const separator = document.createElement('span');
    separator.textContent = ':';
    separator.setAttribute('aria-hidden', 'true');
    const group = document.createElement('span');
    group.className = 'ui-time';
    group.append(hours, separator, minutes);
    input.required = false;
    replace(input, group, hours);

    const sync = () => {
        if (hours.value === '' && minutes.value === '') commit(input, '');
        else if (hours.value !== '' && minutes.value !== '') commit(input, formatTime(Number(hours.value), Number(minutes.value)));
    };
    for (const field of [hours, minutes]) {
        const max = Number(field.dataset.max);
        field.addEventListener('input', () => {
            field.value = cleanPart(field.value, max);
            if (field === hours && field.value.length === 2) minutes.focus();
            sync();
        });
        field.addEventListener('keydown', event => {
            if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
            event.preventDefault();
            const next = ((Number(field.value) || 0) + (event.key === 'ArrowUp' ? 1 : -1) + max + 1) % (max + 1);
            field.value = String(next).padStart(2, '0');
            if (field === minutes && hours.value === '') hours.value = '00';
            sync();
        });
        field.addEventListener('blur', () => { if (field.value.length === 1) { field.value = field.value.padStart(2, '0'); sync(); } });
    }
}

let panels = 0;

function wheel(input: HTMLInputElement): void {
    const display = document.createElement('button');
    display.type = 'button';
    display.className = 'ui-time__display';
    const panel = document.createElement('div');
    panel.className = 'ui-time__panel';
    panel.id = 'time-panel-' + ++panels;
    panel.popover = 'auto';
    panel.setAttribute('role', 'dialog');
    display.setAttribute('popovertarget', panel.id);
    const column = (count: number, label: string) => {
        const list = document.createElement('div');
        list.className = 'ui-time__wheel';
        list.setAttribute('role', 'listbox');
        list.setAttribute('aria-label', label);
        list.append(...Array.from({ length: count }, (_, value) => {
            const option = document.createElement('button');
            option.type = 'button';
            option.className = 'ui-time__option';
            option.setAttribute('role', 'option');
            option.dataset.value = String(value);
            option.textContent = String(value).padStart(2, '0');
            return option;
        }));
        return list;
    };
    const hours = column(24, input.dataset.timeHour ?? '');
    const minutes = column(60, input.dataset.timeMinute ?? '');
    const done = document.createElement('button');
    done.type = 'button';
    done.className = 'button button--primary';
    done.textContent = input.dataset.timeDone ?? '';
    done.setAttribute('popovertarget', panel.id);
    done.setAttribute('popovertargetaction', 'hide');
    const wheels = document.createElement('div');
    wheels.className = 'ui-time__wheels';
    wheels.append(hours, minutes);
    panel.append(wheels, done);
    const group = document.createElement('span');
    group.className = 'ui-time';
    group.append(display, panel);
    input.required = false;
    replace(input, group, display);

    let [hour, minute] = parseTime(input.value) ?? [0, 0];
    const render = () => {
        display.textContent = input.value || '--:--';
        for (const [list, selected] of [[hours, hour], [minutes, minute]] as const) {
            list.querySelectorAll<HTMLButtonElement>('[data-value]').forEach(option => option.setAttribute('aria-selected', String(Number(option.dataset.value) === selected)));
        }
    };
    const centre = (list: HTMLElement, value: number) => {
        const option = list.querySelector<HTMLElement>(`[data-value="${value}"]`);
        if (option) list.scrollTop = option.offsetTop - (list.clientHeight - option.offsetHeight) / 2;
    };
    const choose = (list: HTMLElement, value: number) => {
        if (list === hours) hour = value;
        else minute = value;
        commit(input, formatTime(hour, minute));
        render();
    };
    for (const list of [hours, minutes]) {
        list.addEventListener('click', event => {
            const option = (event.target as Element).closest<HTMLElement>('[data-value]');
            if (!option) return;
            choose(list, Number(option.dataset.value));
            centre(list, Number(option.dataset.value));
        });
        // The option resting in the middle after a turn is the choice.
        list.addEventListener('scrollend', () => {
            const middle = list.scrollTop + list.clientHeight / 2;
            const option = Array.from(list.querySelectorAll<HTMLElement>('[data-value]'))
                .reduce((best, item) => Math.abs(item.offsetTop + item.offsetHeight / 2 - middle) < Math.abs(best.offsetTop + best.offsetHeight / 2 - middle) ? item : best);
            if (Number(option.dataset.value) !== (list === hours ? hour : minute)) choose(list, Number(option.dataset.value));
        });
    }
    panel.addEventListener('toggle', event => {
        if ((event as ToggleEvent).newState !== 'open') return;
        centre(hours, hour);
        centre(minutes, minute);
    });
    render();
}
