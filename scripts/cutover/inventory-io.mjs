import fs from 'node:fs';
import assert from 'node:assert/strict';

export const read = name => fs.readFileSync(name, 'utf8').replace(/^\uFEFF/, '');
export const json = name => JSON.parse(read(name));
export function csvRead(name) {
    const rows = []; let row = [], value = '', quoted = false;
    const input = read(name).replace(/\r\n/g, '\n');
    for (let index = 0; index < input.length; index++) {
        const char = input[index];
        if (char === '"') {
            if (quoted && input[index + 1] === '"') { value += '"'; index++; }
            else quoted = !quoted;
        } else if (!quoted && (char === ',' || char === '\n')) {
            row.push(value); value = '';
            if (char === '\n') { if (row.some(Boolean)) rows.push(row); row = []; }
        } else value += char;
    }
    if (value || row.length) { row.push(value); rows.push(row); }
    assert.equal(quoted, false, `Unclosed CSV field: ${name}`);
    const headers = rows.shift();
    assert.equal(new Set(headers).size, headers.length, `Duplicate CSV headers: ${name}`);
    return rows.map(values => {
        assert.equal(values.length, headers.length, `Wrong CSV field count: ${name}`);
        return Object.fromEntries(headers.map((key, index) => [key, values[index]]));
    });
}
export function csvWrite(name, rows) {
    const keys = [...new Set(rows.flatMap(row => Object.keys(row)))];
    const quote = value => '"' + String(value ?? '').replaceAll('"', '""') + '"';
    fs.writeFileSync(name, keys.map(quote).join(',') + '\n' + rows.map(row => keys.map(key => quote(row[key])).join(',')).join('\n') + '\n');
}
export function writeJson(name, data) {
    function format(value, depth) {
        const indent = '  '.repeat(depth);
        if (Array.isArray(value)) return value.length ? '[\n' + value.map(row => indent + '  ' + JSON.stringify(row)).join(',\n') + '\n' + indent + ']' : '[]';
        if (value && typeof value === 'object') return '{\n' + Object.entries(value).map(([key, item]) => indent + '  ' + JSON.stringify(key) + ': ' + format(item, depth + 1)).join(',\n') + '\n' + indent + '}';
        return JSON.stringify(value);
    }
    fs.writeFileSync(name, format(data, 0) + '\n');
}
