'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../src/EditorForm.cs'), 'utf8');
new vm.Script(source.split('<script>')[1].split('</script>')[0]);
const accountSource = source.slice(source.indexOf('let accountEmail ='), source.indexOf('function renderPlayerPosition()'));
const accountContext = vm.createContext({ state: { account: {} }, escapeHtml: String });
vm.runInContext(accountSource, accountContext);
const accountHtml = () => vm.runInContext('renderAccount()', accountContext);
assert.match(accountHtml(), /Получить код/);
assert.match(accountHtml(), /data-account-action="discord"/);
accountContext.state.account.emailCodeRequested = true;
assert.match(accountHtml(), /autocomplete="one-time-code"/);
accountContext.state.account.busy = 'discord';
assert.match(accountHtml(), /data-account-action="cancel"/);
accountContext.state.account = { user: { DisplayName: 'Игрок', Email: 'player@example.com' } };
assert.match(accountHtml(), /Вход выполнен/);
assert.match(accountHtml(), /data-account-action="logout"/);
assert.doesNotMatch(accountHtml(), /data-account-action="discord"/);
const renderer = source.slice(source.indexOf('function renderPlayerPosition()'), source.indexOf('function renderTreasures()'));
const bindings = source.slice(source.indexOf('  const positionSetting = () =>'), source.indexOf('  syncTreasureCaptureLayout();'));
const settingsDetails = (key, title, content) => `<details data-settings-disclosure="${key}"><summary>${title}</summary>${content}</details>`;
for (const center of [false, true]) for (const keepZoom of [false, true]) for (const showLabel of [false, true]) for (const labelContent of ['name', 'position', 'both']) {
    const fields = new Map();
    const messages = [];
    const context = vm.createContext({
        state: { playerPosition: { settings: { CenterOnPlayer: center, KeepZoom: keepZoom, CenteringZoom: 10, MarkerSize: 40, ShowLabel: showLabel, LabelContent: labelContent } } },
        playerPositionShapes: [{ id: 'triangle', name: 'Треугольник' }],
        renderOcrStatus: () => '', escapeHtml: value => value, settingsDetails,
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
    playerPositionShapes: [{ id: 'triangle', name: 'Треугольник' }], escapeHtml: String, renderOcrStatus: () => '', settingsDetails
});
vm.runInContext(renderer, sharingContext);
const sharingHtml = vm.runInContext('renderPlayerPosition()', sharingContext);
assert.match(sharingHtml, /Только для меня/);
assert.match(sharingHtml, /Всем на сайте/);
assert.match(sharingHtml, /Выбранные профили/);
assert.match(sharingHtml, /data-command="selectPlayerPositionRegion"[\s\S]*data-settings-disclosure="position-display"/);
assert.match(sharingHtml, /data-settings-disclosure="position-diagnostics"[\s\S]*data-command="testPlayerPosition"/);
assert.match(sharingHtml, /data-position-profile value="a" checked/);
assert.match(sharingHtml, /data-position-profile value="b" checked/);
assert.doesNotMatch(sharingHtml, /Wrong map/);
sharingContext.state.account = {};
const signedOutHtml = vm.runInContext('renderPlayerPosition()', sharingContext);
assert.match(signedOutHtml, /data-tab="account"/);
assert.match(signedOutHtml, /data-position-centering-block/);
assert.doesNotMatch(signedOutHtml, /Откройте DayZ-Map/);
vm.runInContext(bindings.slice(0, bindings.indexOf('  const savePlayerPositionSettings')), sharingContext);
const chosen = vm.runInContext('positionSetting()', sharingContext);
assert.deepEqual(Array.from(chosen.ProfileIds), ['a', 'b']);
assert.equal(chosen.Visibility, 'profiles');
assert.equal(chosen.AutoSelectMap, true);
assert.equal(chosen.ShowLabel, true);
assert.equal(chosen.LabelContent, 'name');

const views = [];
const markers = [];
const nodes = () => ({
    classList: { add() {} }, append() {}, setAttribute() {}, after() {}, replaceChildren() {}, querySelector() { return null; }
});
const listeners = {};
const overviewWindow = {
    dayzMap: {
        map: {
            getZoom: () => 7, getMinZoom: () => 3, getMaxZoom: () => 10, getContainer: nodes,
            setView(position, zoom) { views.push({ position, zoom }); }
        },
        gameToLeafletCoords: (x, z) => [z, x],
        showPlayerPositionMarker(position) { markers.push(position); },
        clearPlayerPositionMarker() { markers.push(null); }
    }
};
overviewWindow.top = overviewWindow;
const embeddedMap = fs.readFileSync(path.join(__dirname, '../assets/session-map-view.js'), 'utf8');
vm.runInNewContext(embeddedMap, {
    window: overviewWindow,
    location: { origin: 'https://dayz-map.ru', pathname: '/deer-isle/' },
    document: { documentElement: nodes(), createElement: nodes, addEventListener(name, action) { listeners[name] = action; } },
    MutationObserver: class { observe() {} disconnect() {} }
});
const sample = {
    MapId: 'deerisle', X: 4000, Y: 20, Z: 6500, Template: { Name: 'Я', Color: '#3498db', Type: 'triangle' },
    MarkerSize: 32, CenterOnPlayer: true, KeepZoom: true, CenteringZoom: 12
};
overviewWindow.DayZCompanionOverviewPosition(sample);
assert.equal(views.length, 0);
listeners.DOMContentLoaded();
assert.deepEqual(views[0], { position: [6500, 4000], zoom: 7 });
overviewWindow.DayZCompanionOverviewPosition({ ...sample, KeepZoom: false });
assert.deepEqual(views[1], { position: [6500, 4000], zoom: 10 });
overviewWindow.DayZCompanionOverviewPosition({ ...sample, X: 4100, CenterOnPlayer: false });
assert.deepEqual(markers.at(-1), [6500, 4100]);
assert.equal(views.length, 2);
overviewWindow.DayZCompanionOverviewPosition({ ...sample, MapId: 'cherno' });
assert.equal(markers.at(-1), null);
overviewWindow.DayZCompanionOverviewPosition(sample);
overviewWindow.DayZCompanionOverviewPosition(null);
assert.equal(markers.at(-1), null);
console.log('PASS player-position UI: dependencies, privacy modes, multi-profile, map filtering and JavaScript syntax');
