'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../src/EditorForm.cs'), 'utf8');
new vm.Script(source.split('<script>')[1].split('</script>')[0]);
const renderer = source.slice(source.indexOf('function renderUpdates()'), source.indexOf('function bindEditorEvents()'));
const context = vm.createContext({ state: {}, escapeHtml: s => String(s).replaceAll('<', '&lt;'), renderMarkdown: s => s,
    field: (label, options) => `<section>${label}${options.input}</section>` });
vm.runInContext(renderer, context);
const render = (info, operation = {}, store = false) => {
    context.state = { microsoftStore: store, update: info, updateProgress: operation };
    return vm.runInContext('renderUpdates()', context);
};
const info = { CanInstall: true, IsUpdateAvailable: true, InstallerUrl: 'https://example.com/installer.exe', CurrentVersion: '1', LatestVersion: '2' };
for (const busy of [false, true]) {
    const html = render(info, { IsBusy: busy, CanCancel: busy, Message: 'Скачивание — 50%', Percent: 50 });
    assert.match(html, /data-command="downloadUpdate"/);
    assert.equal(/data-command="installUpdate" disabled/.test(html), busy);
    if (busy) {
        assert.match(html, /value="50"/);
        assert.match(html, /data-command="cancelUpdate"/);
    }
}
let html = render({ ...info, CanInstall: false });
assert.match(html, /data-command="installUpdate" disabled/);
assert.doesNotMatch(html, /data-command="downloadUpdate" disabled/);
html = render(info, { IsBusy: true, Message: 'Проверка обновления…' });
assert.match(html, /<progress[^>]*max="100"\s*>/);
assert.doesNotMatch(html, /data-command="cancelUpdate"/);
html = render(info, { IsBusy: false, Message: 'Ошибка <network>' });
assert.match(html, /Ошибка &lt;network>/);
html = render(info, {}, true);
assert.match(html, /Microsoft Store/);
assert.doesNotMatch(html, /data-command="installUpdate"/);
console.log('PASS update UI: primary update, installer fallback, progress, cancellation, errors and Store');
