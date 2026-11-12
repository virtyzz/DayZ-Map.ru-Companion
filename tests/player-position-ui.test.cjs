'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../src/EditorForm.cs'), 'utf8');
new vm.Script(source.split('<script>')[1].split('</script>')[0]);
const renderer = source.slice(source.indexOf('function renderPlayerPosition()'), source.indexOf('function renderTreasures()'));
const bindings = source.slice(source.indexOf('  const positionSetting = () =>'), source.indexOf('  document.querySelectorAll("[data-treasure]")'));
for (const center of [false, true]) for (const keepZoom of [false, true]) for (const showLabel of [false, true]) for (const labelContent of ['name', 'position', 'both']) {
    const fields = new Map();
    const messages = [];
    const context = vm.createContext({
        state: { playerPosition: { settings: { CenterOnPlayer: center, KeepZoom: keepZoom, CenteringZoom: 10, MarkerSize: 40, ShowLabel: showLabel, LabelContent: labelContent } } },
        playerPositionShapes: [{ id: 'triangle', name: 'Треугольник' }],
        renderOcrStatus: () => '', escapeHtml: value => value,
        playerPositionSettingsTimer: null, clearTimeout() {}, setTimeout() {},
        post: message => messages.push(message),
        document: {
            querySelector: selector => fields.get(selector) || null,
            querySelectorAll: selectors => selectors.split(', ').map(selector => fields.get(selector)).filter(Boolean)
        }
    });
    vm.runInContext(renderer, context);
    const render = () => {
        const html = vm.runInContext('renderPlayerPosition()', context);
        const group = html.match(/<div class="field" data-position-centering-block>([\s\S]*?)<\/div>/)[1];
        for (const key of ['center', 'keep-zoom', 'zoom']) assert.match(group, new RegExp(`data-position-${key}\\b`));
        assert.doesNotMatch(group, /class="field"/, 'centering controls share a single field');
        fields.clear();
        for (const match of html.matchAll(/<input\b[^>]*data-position-[^>]*>/g)) {
            const text = match[0];
            const key = `[${text.match(/data-position-[\w-]+/)[0]}]`;
            fields.set(key, { checked: /\bchecked\b/.test(text), disabled: /\bdisabled\b/.test(text),
                value: text.match(/value="([^"]*)"/)?.[1] || '', events: {},
                addEventListener(name, listener) { this.events[name] = listener; } });
        }
        assert.doesNotMatch(html, /data-position-(auto-map|map|label|label-content)\b/, 'removed controls must not be rendered');
    };
    context.render = render;
    render();
    assert.equal(fields.get('[data-position-keep-zoom]').disabled, !center);
    assert.equal(fields.get('[data-position-zoom]').disabled, !center || keepZoom);
    vm.runInContext(bindings, context);
    fields.get('[data-position-center]').checked = !center;
    fields.get('[data-position-center]').events.change();
    assert.equal(messages.at(-1).settings.CenterOnPlayer, !center);
    assert.equal(messages.at(-1).settings.KeepZoom, keepZoom);
    assert.equal(messages.at(-1).settings.CenteringZoom, 10);
    assert.equal(messages.at(-1).settings.MarkerSize, 40);
    assert.equal(messages.at(-1).settings.ShowLabel, true);
    assert.equal(messages.at(-1).settings.LabelContent, 'name');
    assert.equal(fields.get('[data-position-keep-zoom]').disabled, center);
    assert.equal(fields.get('[data-position-zoom]').disabled, center || keepZoom);

}
const sharingFields = new Map([
    ['[data-position-visibility]', { value: 'profiles' }],
    ['[data-position-auto-map]', { checked: false }],
    ['[data-position-map]', { value: 'cherno' }]
]);
const selectedProfiles = [{ value: 'a', checked: true }, { value: 'b', checked: true }, { value: 'c', checked: false }];
const sharingContext = vm.createContext({
    state: { playerPosition: { settings: { MapId: 'cherno', Visibility: 'profiles', ProfileIds: ['a', 'b'] }, destination: {
        Maps: [{ Id: 'cherno', Name: 'Черноруссия' }],
        Profiles: [{ Id: 'a', MapId: 'cherno', Name: 'A' }, { Id: 'b', MapId: 'cherno', Name: 'B' }, { Id: 'hidden', MapId: 'deadfall', Name: 'Wrong map' }]
    } } },
    document: { querySelector: selector => sharingFields.get(selector), querySelectorAll: selector => selector === '[data-position-profile]' ? selectedProfiles : [] },
    playerPositionShapes: [{ id: 'triangle', name: 'Треугольник' }], escapeHtml: String, renderOcrStatus: () => ''
});
vm.runInContext(renderer, sharingContext);
const sharingHtml = vm.runInContext('renderPlayerPosition()', sharingContext);
assert.match(sharingHtml, /Только для меня/);
assert.match(sharingHtml, /Всем на сайте/);
assert.match(sharingHtml, /Выбранные профили/);
assert.match(sharingHtml, /data-position-profile value="a" checked/);
assert.match(sharingHtml, /data-position-profile value="b" checked/);
assert.doesNotMatch(sharingHtml, /Wrong map/);
vm.runInContext(bindings.slice(0, bindings.indexOf('  const savePlayerPositionSettings')), sharingContext);
const chosen = vm.runInContext('positionSetting()', sharingContext);
assert.deepEqual(Array.from(chosen.ProfileIds), ['a', 'b']);
assert.equal(chosen.Visibility, 'profiles');
assert.equal(chosen.AutoSelectMap, true);
assert.equal(chosen.ShowLabel, true);
assert.equal(chosen.LabelContent, 'name');
console.log('PASS player-position UI: dependencies, privacy modes, multi-profile, map filtering and JavaScript syntax');
