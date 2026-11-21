const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync, spawn } = require('node:child_process');
const { once } = require('node:events');
const { resolve } = require('node:path');
const { createInterface } = require('node:readline');
const { chromium } = require('playwright-core');

const projectDirectory = resolve(__dirname, '../../src/SprintScore');
let serverAssembly;
let browser;
before(async () => {
    serverAssembly = execFileSync('dotnet', ['msbuild', '-nologo', '-p:Configuration=Release', '-getProperty:TargetPath'],
        { cwd: projectDirectory, encoding: 'utf8', windowsHide: true }).trim();
    browser = await chromium.launch({ channel: process.env.SPRINTSCORE_BROWSER_CHANNEL || undefined });
});
after(async () => { await browser?.close(); });

// Each test owns its in-memory round and a port assigned by Kestrel.
async function startServer(t) {
    const server = spawn('dotnet', [serverAssembly, '--urls', 'http://127.0.0.1:0'],
        { cwd: projectDirectory, windowsHide: true, stdio: ['ignore', 'pipe', 'inherit'] });
    const closed = once(server, 'close');
    t.after(async () => { server.kill(); await closed; });
    const lines = createInterface({ input: server.stdout });
    const timer = setTimeout(() => server.kill(), 20000);
    try {
        for await (const line of lines) {
            const match = line.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/);
            if (match) return match[1];
        }
        throw new Error('Kestrel exited before reporting its listening address');
    } finally {
        clearTimeout(timer);
        lines.close();
        server.stdout.resume();
    }
}

async function pageAt(t, url) {
    const context = await browser.newContext();
    t.after(() => context.close());
    const page = await context.newPage();
    await page.goto(url);
    return page;
}

async function expectText(page, selector, text) {
    await page.waitForFunction(([selector, text]) => document.querySelector(selector)?.textContent === text,
        [selector, text]);
}

// Use the shipped SignalR client without starting the page's voting UI.
async function hubClient(t, url, admin = false, participantId = null) {
    const page = await pageAt(t, url + '/healthz');
    await page.addScriptTag({ url: url + '/lib/signalr/signalr.min.js' });
    await page.evaluate(({ admin, participantId }) => {
        const query = new URLSearchParams({ participantId: participantId ?? crypto.randomUUID() });
        if (admin) query.set('role', 'admin');
        window.updates = [];
        window.disconnected = false;
        window.connection = new signalR.HubConnectionBuilder().withUrl('/hub?' + query).build();
        connection.on('Update', update => updates.push(update));
        connection.onclose(() => { window.disconnected = true; });
    }, { admin, participantId });
    return page;
}

function invoke(page, ...args) {
    return page.evaluate(args => connection.invoke(...args), args);
}

test('SignalR sends each voter only their own vote until reveal', async t => {
    const url = await startServer(t);
    const alice = await hubClient(t, url);
    const bob = await hubClient(t, url);
    const host = await hubClient(t, url, true);
    for (const page of [alice, bob, host]) await page.evaluate(() => connection.start());
    assert.equal(await invoke(alice, 'Vote', 1, 3), 'accepted');
    assert.equal(await invoke(bob, 'Vote', 1, 9), 'accepted');
    for (const [page, own] of [[alice, 3], [bob, 9], [host, null]]) {
        await page.waitForFunction(() => updates.at(-1)?.state.votedCount === 2);
        for (const update of await page.evaluate(() => updates)) {
            const { state, revision, self } = update;
            assert.equal(typeof revision, 'number');
            const expectedVote = state.votedCount >= (page === bob ? 2 : 1) ? own : null;
            assert.deepEqual(update, {
                revision, self: { round: 1, myVote: expectedVote },
                state: { round: 1, revealed: false, onlineCount: state.onlineCount,
                    totalCount: state.onlineCount, votedCount: state.votedCount, average: null, distribution: null }
            });
        }
    }
    assert.equal(await invoke(host, 'Reveal', 1, null), 'accepted');
    for (const page of [alice, bob, host]) {
        await page.waitForFunction(() => updates.at(-1)?.state.revealed);
        const state = await page.evaluate(() => updates.at(-1).state);
        assert.equal(state.average, 6);
        assert.deepEqual(state.distribution, [0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);
    }
});

test('hub enforces command roles and rejects a malformed participant ID', async t => {
    const url = await startServer(t);
    const voter = await hubClient(t, url);
    const host = await hubClient(t, url, true);
    for (const page of [voter, host]) await page.evaluate(() => connection.start());
    assert.equal(await invoke(voter, 'Vote', 1, 7), 'accepted');
    for (const args of [['SetAdminVote', 1, 4], ['Reveal', 1, null], ['Restart', 1]]) {
        await assert.rejects(invoke(voter, ...args), /Действие недоступно для этой роли/);
    }
    for (const args of [['Vote', 1, 6], ['RetractVote', 1]]) {
        await assert.rejects(invoke(host, ...args), /Действие недоступно для этой роли/);
    }
    const invalid = await hubClient(t, url, false, 'invalid-id');
    await invalid.evaluate(async () => {
        try { await connection.start(); }
        catch { window.disconnected = true; }
    });
    await invalid.waitForFunction(() => disconnected);
    assert.deepEqual(await invalid.evaluate(() => updates), []);

    // A fresh publication exposes any unintended changes from rejected commands.
    const revision = await voter.evaluate(() => updates.at(-1).revision);
    assert.equal(await invoke(host, 'SetAdminVote', 1, null), 'accepted');
    await voter.waitForFunction(previous => updates.at(-1).revision > previous, revision);
    assert.equal(await voter.evaluate(() => updates.at(-1).self.myVote), 7);
    assert.equal(await voter.evaluate(() => updates.every(u => u.state.onlineCount <= 2)), true);
    const snapshot = await host.evaluate(() => updates.at(-1).state);
    assert.equal(snapshot.onlineCount, 2);
    assert.equal(snapshot.votedCount, 1);
    assert.equal(snapshot.revealed, false);
    assert.equal(await invoke(voter, 'RetractVote', 1), 'accepted');
    await host.waitForFunction(() => updates.at(-1)?.state.votedCount === 0);
    assert.equal(await invoke(host, 'Restart', 1), 'accepted');
    await voter.waitForFunction(() => updates.at(-1)?.state.round === 2);
});

test('participant and host complete a round and vote again after restart', async t => {
    const url = await startServer(t);
    const participant = await pageAt(t, url);
    const host = await pageAt(t, url + '/admin');
    const pages = [participant, host];
    for (const page of pages) await expectText(page, '#online', '2');
    await participant.click('[data-score="3"]');
    for (const page of pages) await expectText(page, '#voted', '0');
    await participant.click('#confirm');
    for (const page of pages) await expectText(page, '#voted', '1');
    await participant.click('#retract');
    for (const page of pages) await expectText(page, '#voted', '0');
    await participant.click('[data-score="7"]');
    await participant.click('#confirm');
    await host.fill('#admin-vote', '9');
    for (const page of pages) {
        await expectText(page, '#voted', '2');
        assert.equal(await page.locator('#average').isHidden(), true);
        await expectText(page, '[data-count="7"]', '');
    }
    await host.click('#reveal');
    for (const page of pages) {
        await expectText(page, '#average', '~ 8.00');
        assert.equal(await page.locator('#average').isVisible(), true);
        await expectText(page, '[data-count="7"]', '1');
        await expectText(page, '[data-count="9"]', '1');
    }
    await host.click('#restart');
    for (const page of pages) {
        await expectText(page, '#voted', '0');
        assert.equal(await page.locator('#average').isHidden(), true);
    }
    await participant.click('[data-score="1"]');
    await participant.click('#confirm');
    await host.fill('#admin-vote', '10');
    for (const page of pages) await expectText(page, '#voted', '2');
});

test('invalid host input and the explicit clear button both exclude the previous vote', async t => {
    const url = await startServer(t);
    const host = await pageAt(t, url + '/admin');
    await expectText(host, '#online', '1');
    for (const action of ['invalid', 'clear']) {
        await host.fill('#admin-vote', '8');
        await expectText(host, '#admin-vote-status', 'Оценка сохранена');
        await expectText(host, '#voted', '1');
        if (action === 'invalid') {
            await host.fill('#admin-vote', '11');
            await expectText(host, '#admin-vote-status', 'Введите целое число от 1 до 10');
            assert.equal(await host.locator('#admin-vote').getAttribute('aria-invalid'), 'true');
        } else {
            await host.click('#admin-vote-clear');
            assert.equal(await host.inputValue('#admin-vote'), '');
        }
        await expectText(host, '#voted', '0');
        await host.click('#reveal');
        await expectText(host, '#average', 'нет голосов');
        await host.click('#restart');
        await host.locator('#average').waitFor({ state: 'hidden' });
    }
});

test('participant reconnects after a WebSocket interruption and can retract the restored vote', async t => {
    const url = await startServer(t);
    const host = await pageAt(t, url + '/admin');
    const context = await browser.newContext();
    t.after(() => context.close());
    const participant = await context.newPage();
    let socket;
    let upstream;
    const reconnect = Promise.withResolvers();
    await participant.routeWebSocket('**/hub?**', async route => {
        if (socket) await reconnect.promise;
        socket = route;
        upstream = route.connectToServer();
    });
    await participant.goto(url);
    await expectText(participant, '#online', '2');
    await participant.click('[data-score="8"]');
    await participant.click('#confirm');
    await expectText(host, '#voted', '1');
    await socket.close({ code: 1011, reason: 'test interruption' });
    await upstream.close();
    try {
        await participant.locator('#reconnect-banner').waitFor({ state: 'visible' });
        await expectText(host, '#online', '1');
    } finally {
        reconnect.resolve();
    }
    await participant.locator('#reconnect-banner').waitFor({ state: 'hidden' });
    await expectText(host, '#online', '2');
    await expectText(participant, '#pick', ', 8');
    await participant.click('#retract');
    await expectText(host, '#voted', '0');
});
