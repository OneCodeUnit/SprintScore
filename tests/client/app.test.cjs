const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/SprintScore/wwwroot/js/app.js'), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));

function client(role = 'admin') {
    const elements = {}, events = {}, requests = [];
    let starts = 0;
    function element() {
        return { value: '', textContent: '', dataset: {}, classList: { toggle() {}, add() {} },
            setAttribute() {}, replaceChildren() {}, appendChild() {}, focus() {},
            addEventListener(name, fn) { this[name] = fn; } };
    }
    const hub = {
        on(name, fn) { events[name] = fn; },
        onreconnecting(fn) { events.reconnecting = fn; },
        onreconnected() {}, onclose() {},
        start() { starts++; return Promise.resolve(); },
        invoke(...args) {
            return new Promise((resolve, reject) => requests.push({ args, resolve, reject }));
        }
    };
    function Builder() {
        this.withUrl = this.withAutomaticReconnect = () => this;
        this.build = () => hub;
    }
    vm.runInNewContext(source, {
        document: { body: { dataset: { role } },
            getElementById: id => elements[id] ??= element(),
            querySelectorAll: () => [], querySelector: element, createElement: element },
        localStorage: { getItem: () => '11111111-1111-4111-8111-111111111111', setItem() {} },
        crypto: {}, signalR: { HubConnectionBuilder: Builder }, console: { error() {} }
    });
    function update(revision, vote, round = 1) {
        events.Update({ revision, self: { myVote: vote }, state: {
            round, revealed: false, onlineCount: 2, totalCount: 2, votedCount: vote === null ? 0 : 1
        } });
    }
    return { elements, requests, events, update, get starts() { return starts; } };
}

test('a delayed save reconciles a newer vote before queued reveal', async () => {
    const c = client();
    c.update(1, null);
    c.elements['admin-vote'].value = '8';
    c.elements['admin-vote'].input();
    await tick();
    c.update(2, 8);
    c.update(3, 4); // другая вкладка записала более новую оценку
    c.elements.reveal.click();
    c.requests[0].resolve('accepted');
    await tick();
    assert.equal(c.elements['admin-vote'].value, '4');
    assert.deepEqual(c.requests[1].args, ['Reveal', 1, 4]);
    c.requests[1].resolve('accepted');
});

test('acknowledging an older edit does not replace new keyboard input', async () => {
    const c = client(); c.update(1, null);
    c.elements['admin-vote'].value = '1'; c.elements['admin-vote'].input();
    await tick(); c.update(2, 1);
    c.elements['admin-vote'].value = '10'; c.elements['admin-vote'].input();
    c.requests[0].resolve('accepted'); await tick();
    assert.equal(c.elements['admin-vote'].value, '10');
    c.update(3, 10); c.requests[1].resolve('accepted'); await tick();
    assert.equal(c.elements['admin-vote-status'].textContent, 'Оценка сохранена');
});

test('updates after a round reset ignore old acknowledgements', async () => {
    const c = client(); c.update(1, null);
    c.elements['admin-vote'].value = '8'; c.elements['admin-vote'].input();
    await tick(); c.update(2, null, 2);
    c.requests[0].resolve('accepted'); await tick();
    c.update(1, 8);
    assert.equal(c.elements['admin-vote'].value, '');
    assert.equal(c.elements['admin-vote-status'].textContent, '');
});

test('error and unknown pages never connect to the hub', () => {
    assert.equal(client('none').starts, 0);
    assert.equal(client('error').starts, 0);
});

test('failed saves are not shown as saved', async () => {
    const c = client(); c.update(1, null);
    c.elements['admin-vote'].value = '8'; c.elements['admin-vote'].input();
    await tick(); c.requests[0].reject(new Error('offline')); await tick();
    assert.equal(c.elements['admin-vote-status'].textContent, 'Не удалось сохранить');
    assert.equal(c.elements['admin-vote'].value, '8');
});
