'use strict';

// Automatiskt test: startar servern, kopplar upp simulerade klienter och kontrollerar flödet.
// Kör med: npm test

const assert = require('assert');
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const net = require('net');

if (typeof WebSocket === 'undefined') {
  console.error('Testet kräver Node 22 eller nyare (inbyggd WebSocket-klient).');
  process.exit(1);
}

const PORT = 18080;
const ADMIN_KEY = 'testnyckel';
const INTEREST_WAIT_MS = 1200;
const URL = `ws://localhost:${PORT}`;
const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'syncup-test-'));

class Client {
  constructor() {
    this.ws = new WebSocket(URL);
    this.msgs = [];
    this.waiters = [];
    this.ws.addEventListener('message', (ev) => {
      const m = JSON.parse(ev.data);
      const i = this.waiters.findIndex((w) => w.type === m.type);
      if (i >= 0) this.waiters.splice(i, 1)[0].resolve(m);
      else this.msgs.push(m);
    });
  }
  open() {
    return new Promise((resolve, reject) => {
      this.ws.addEventListener('open', resolve);
      this.ws.addEventListener('error', () => reject(new Error('Kunde inte ansluta')));
    });
  }
  send(obj) {
    this.ws.send(JSON.stringify(obj));
  }
  expect(type, ms = 2500) {
    const idx = this.msgs.findIndex((m) => m.type === type);
    if (idx >= 0) return Promise.resolve(this.msgs.splice(idx, 1)[0]);
    return new Promise((resolve, reject) => {
      const waiter = { type, resolve };
      this.waiters.push(waiter);
      setTimeout(() => {
        const i = this.waiters.indexOf(waiter);
        if (i >= 0) {
          this.waiters.splice(i, 1);
          reject(new Error(`Timeout: väntade på "${type}"`));
        }
      }, ms);
    });
  }
  async expectNone(type, ms = 1200) {
    try {
      await this.expect(type, ms);
    } catch {
      return;
    }
    throw new Error(`Fick oväntat "${type}"`);
  }
  close() {
    this.ws.close();
  }
}

async function connect() {
  const c = new Client();
  await c.open();
  return c;
}

const ok = (text) => console.log('  ✓ ' + text);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Anropar adminpanelens API. */
async function adminApi(pathname, body, key = ADMIN_KEY) {
  const res = await fetch(`http://127.0.0.1:${PORT}/admin/api/${pathname}`, {
    method: body ? 'POST' : 'GET',
    headers: { 'X-Admin-Key': key, 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: res.status, data: await res.json() };
}

/** Ett meddelande med samma form som Unitys JsonUtility skickar: alla fält med, tomma om de inte används. */
const unityMsg = (fields) => ({
  type: '', name: '', password: '', text: '', sender: '', partner: '', reason: '', error: '', mode: '',
  online: 0, interests: [], common: [], on: false, bot: false, ...fields,
});

/** Ansluter anonymt och returnerar klienten och dess namn. */
async function anonClient(mk) {
  const c = await mk();
  c.send({ type: 'anon' });
  c.name = (await c.expect('welcome')).name;
  return c;
}

// Bygger en maskad klientram (som riktiga klienter skickar) för korta meddelanden
function maskedFrame(opcode, text, fin = true) {
  const payload = Buffer.from(text);
  const mask = crypto.randomBytes(4);
  const masked = Buffer.alloc(payload.length);
  for (let i = 0; i < payload.length; i++) masked[i] = payload[i] ^ mask[i % 4];
  const header = Buffer.from([(fin ? 0x80 : 0) | opcode, 0x80 | payload.length]);
  return Buffer.concat([header, mask, masked]);
}

// Pratar rå TCP med servern: handskakning, fragmenterat meddelande med ping mitt i
function rawProtocolTest() {
  return new Promise((resolve, reject) => {
    const key = crypto.randomBytes(16).toString('base64');
    const expected = crypto
      .createHash('sha1')
      .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')
      .digest('base64');
    const sock = net.connect(PORT, '127.0.0.1');
    let received = Buffer.alloc(0);
    let handshakeDone = false;
    const fail = (err) => {
      clearTimeout(timer);
      sock.destroy();
      reject(err);
    };
    const timer = setTimeout(() => fail(new Error('Timeout i protokolltestet')), 3000);

    sock.on('error', fail);
    sock.on('connect', () => {
      sock.write(
        `GET / HTTP/1.1\r\nHost: localhost:${PORT}\r\nUpgrade: websocket\r\n` +
          `Connection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`
      );
    });
    sock.on('data', (chunk) => {
      received = Buffer.concat([received, chunk]);
      const text = received.toString('latin1');
      if (!handshakeDone) {
        if (!text.includes('\r\n\r\n')) return;
        try {
          assert(text.startsWith('HTTP/1.1 101'));
          assert(text.includes('Sec-WebSocket-Accept: ' + expected));
        } catch (err) {
          return fail(err);
        }
        handshakeDone = true;
        received = Buffer.alloc(0);
        sock.write(maskedFrame(0x1, '{"type":"an', false)); // första delen
        sock.write(maskedFrame(0x9, 'x'));                    // ping mitt i meddelandet
        sock.write(maskedFrame(0x0, 'on"}', true));           // sista delen
        return;
      }
      const gotPong = received.includes(Buffer.from([0x8a, 0x01, 0x78]));
      if (gotPong && text.includes('"type":"welcome"')) {
        clearTimeout(timer);
        sock.destroy();
        resolve();
      }
    });
  });
}

/** Startar en server med de inställningar som anges och väntar tills den lyssnar. */
async function startServer(env) {
  const child = spawn('node', ['server.js'], {
    cwd: __dirname,
    env: { ...process.env, ...env },
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  await new Promise((resolve, reject) => {
    child.stdout.on('data', (d) => d.toString().includes('lyssnar') && resolve());
    child.on('exit', () => reject(new Error('Servern startade inte')));
  });
  return child;
}

async function main() {
  const server = await startServer({
    PORT: String(PORT),
    DATA_DIR: dataDir,
    ADMIN_KEY,
    INTEREST_WAIT_MS: String(INTEREST_WAIT_MS),
    BOT_SPEED: '0.05',
  });

  const sockets = [];
  const mk = async () => {
    const c = await connect();
    sockets.push(c);
    return c;
  };

  try {
    console.log('Anonymt läge');
    const a = await mk();
    const b = await mk();
    a.send({ type: 'anon' });
    b.send({ type: 'anon' });
    const wa = await a.expect('welcome');
    const wb = await b.expect('welcome');
    assert(/^Anonym#\d{4}$/.test(wa.name) && wa.name !== wb.name);
    ok(`anonyma namn: ${wa.name}, ${wb.name}`);

    a.send({ type: 'find' });
    b.send({ type: 'find' });
    const ma = await a.expect('matched');
    const mb = await b.expect('matched');
    assert.strictEqual(ma.partner, wb.name);
    assert.strictEqual(mb.partner, wa.name);
    ok('två anonyma matchas med varandra');

    a.send({ type: 'chat', text: 'hej där' });
    assert.strictEqual((await b.expect('chat')).text, 'hej där');
    assert.strictEqual((await a.expect('chat')).sender, wa.name);
    ok('meddelanden når båda');

    a.send({ type: 'chat', text: 'ring 070 123 45 67 eller @kalle på kalle@mail.se, fuck <b>' });
    const filtered = (await b.expect('chat')).text;
    assert(!/\d{3}/.test(filtered) && !filtered.includes('@') && !/fuck/i.test(filtered));
    assert(filtered.includes('[dolt]') && filtered.includes('****'));
    await a.expect('info'); // varning om dolt innehåll
    ok(`filter: "${filtered}"`);

    a.send({ type: 'next' });
    await b.expect('partner_left');
    await a.expect('searching');
    ok('"Nästa" meddelar den andra personen');

    b.send({ type: 'report', reason: 'testrapport' });
    assert(/sparats/.test((await b.expect('info')).text));
    const log = fs.readFileSync(path.join(dataDir, 'reports.log'), 'utf8');
    assert(log.includes(`Rapporterad användare: ${wa.name}`) && log.includes('hej där'));
    ok('rapport (även efter att samtalet slutat) skrivs till reports.log');

    const c = await mk();
    c.send({ type: 'anon' });
    await c.expect('welcome');
    c.send({ type: 'find' });
    assert.strictEqual((await c.expect('matched')).partner, wa.name);
    ok('ny person matchas direkt med den som sökte');
    c.close();
    await a.expect('partner_left');

    console.log('Konton');
    const al = await mk();
    const bo = await mk();
    al.send({ type: 'register', name: 'alice', password: 'hemligt1' });
    assert.strictEqual((await al.expect('auth_ok')).name, 'alice');
    bo.send({ type: 'register', name: 'bob', password: 'hemligt2' });
    await bo.expect('auth_ok');
    ok('registrering fungerar');

    const dup = await mk();
    dup.send({ type: 'register', name: 'ALICE', password: 'annatlosen' });
    assert(/upptaget/.test((await dup.expect('auth_error')).error));
    dup.send({ type: 'login', name: 'alice', password: 'fel lösen' });
    assert(/Fel/.test((await dup.expect('auth_error')).error));
    dup.send({ type: 'login', name: 'alice', password: 'hemligt1' });
    assert(/redan inloggat/.test((await dup.expect('auth_error')).error));
    dup.send({ type: 'register', name: 'Anonym1234', password: 'hemligt3' });
    assert(/reserverat/.test((await dup.expect('auth_error')).error));
    ok('upptaget namn, fel lösenord, dubbel inloggning och reserverade namn nekas');

    const users = fs.readFileSync(path.join(dataDir, 'users.json'), 'utf8');
    assert(!users.includes('hemligt1'));
    ok('lösenord sparas inte i klartext');

    const x = await mk();
    x.send({ type: 'anon' });
    await x.expect('welcome');
    x.send({ type: 'find' });
    al.send({ type: 'find' });
    await al.expectNone('matched');
    await x.expectNone('matched', 300);
    ok('anonym och konto blandas aldrig');

    bo.send({ type: 'find' });
    assert.strictEqual((await al.expect('matched')).partner, 'bob');
    await bo.expect('matched');
    ok('två konton matchas med varandra');

    for (let i = 0; i < 25; i++) al.send({ type: 'chat', text: 'spam ' + i });
    assert(/snabbt/.test((await al.expect('info', 3000)).text));
    ok('flodskydd aktiveras vid spam');

    console.log('Protokoll (egen WebSocket-implementation)');
    await rawProtocolTest();
    ok('handskakning, fragmenterade meddelanden och ping/pong fungerar');

    x.close();
    await new Promise((resolve) => setTimeout(resolve, 300));
    const d = await mk();
    const e = await mk();
    d.send({ type: 'anon' });
    e.send({ type: 'anon' });
    await d.expect('welcome');
    await e.expect('welcome');
    d.send({ type: 'find' });
    e.send({ type: 'find' });
    await d.expect('matched');
    await e.expect('matched');
    const longText = 'å'.repeat(250); // 500 byte, kräver 16-bitars längdfält
    d.send({ type: 'chat', text: longText });
    assert.strictEqual((await e.expect('chat')).text, longText);
    ok('långa meddelanden (över 125 byte) skickas korrekt');


    console.log('syncBot');
    const solo = await anonClient(mk);
    solo.send({ type: 'find' });
    await solo.expect('searching');
    solo.send({ type: 'bot' });
    const mBot = await solo.expect('matched');
    assert.strictEqual(mBot.partner, 'syncBot');
    assert.strictEqual(mBot.bot, true);
    assert.strictEqual((await solo.expect('chat')).sender, 'syncBot');
    ok('man kan chatta med syncBot och får en hälsning');

    solo.send({ type: 'chat', text: 'hejdå' });
    await solo.expect('chat'); // eget meddelande
    assert.strictEqual((await solo.expect('typing')).on, true);
    assert(/Hejdå/.test((await solo.expect('chat')).text));
    ok('syncBot visar "skriver..." och svarar på det man skriver');

    solo.send({ type: 'next' });
    await solo.expect('searching');
    await solo.expectNone('chat', 400);
    ok('boten slutar svara när man trycker Nästa');
    solo.close();

    console.log('Intressen');
    const p = await anonClient(mk);
    const q = await anonClient(mk);
    const searchStart = Date.now(); // väntetiden räknas från när de första började söka
    p.send({ type: 'find', interests: ['musik', 'påhittat'] });
    q.send({ type: 'find', interests: ['spel'] });
    await p.expectNone('matched', 400);
    ok('olika intressen matchas inte direkt');

    const r = await anonClient(mk);
    r.send({ type: 'find', interests: ['spel', 'musik'] });
    const mr = await r.expect('matched');
    assert.deepStrictEqual(mr.common, [mr.partner === p.name ? 'musik' : 'spel']);
    ok(`gemensamt intresse matchas och skickas med (${mr.common[0]})`);

    const left = mr.partner === p.name ? q : p;
    const t = await anonClient(mk);
    t.send({ type: 'find', interests: ['djur'] });
    await left.expect('matched', INTEREST_WAIT_MS + 2500);
    assert(Date.now() - searchStart >= INTEREST_WAIT_MS - 50);
    ok('efter en stund matchas man med vem som helst');

    console.log('Skriver... och isbrytare');
    t.send({ type: 'typing', on: true });
    assert.strictEqual((await left.expect('typing')).on, true);
    t.send({ type: 'typing', on: false });
    assert.strictEqual((await left.expect('typing')).on, false);
    ok('"skriver..." skickas vidare till den andra');

    t.send({ type: 'icebreaker' });
    const iceT = await t.expect('icebreaker');
    const iceL = await left.expect('icebreaker');
    assert(iceT.text.endsWith('?') && iceT.text === iceL.text && iceT.sender === t.name);
    t.send({ type: 'icebreaker' });
    assert(/Vänta/.test((await t.expect('info')).text));
    ok('isbrytaren når båda, med spärr mot spam');

    console.log('Adminpanel');
    assert.strictEqual((await adminApi('state', null, 'fel')).status, 401);
    const page = await fetch(`http://127.0.0.1:${PORT}/admin?key=${ADMIN_KEY}`);
    assert(page.ok && (await page.text()).includes('<title>syncUp – Adminpanel</title>'));
    ok('adminpanelen kräver rätt nyckel');

    const { data: snap } = await adminApi('state');
    assert(snap.online >= 4 && snap.rooms.length >= 2 && snap.accounts.some((a) => a.name === 'alice'));
    assert(!JSON.stringify(snap.rooms).includes('hej där'));
    assert(snap.reports.length === 1 && snap.reports[0].includes('testrapport'));
    ok(`översikten visar ${snap.online} online, ${snap.rooms.length} chattar, konton och rapporter`);

    await adminApi('announce', { text: 'Testmeddelande' });
    assert.strictEqual((await t.expect('announce')).text, 'Testmeddelande');
    ok('meddelande till alla kommer fram');

    await adminApi('kick', { name: t.name });
    assert(/admin/.test((await t.expect('kicked')).text));
    await left.expect('partner_left');
    assert.strictEqual((await adminApi('kick', { name: 'finnsinte' })).status, 400);
    ok('kick kopplar bort användaren');

    await adminApi('ban', { name: 'bob' });
    await bo.expect('kicked');
    const banned = await mk();
    banned.send({ type: 'login', name: 'bob', password: 'hemligt2' });
    assert(/avstängt/.test((await banned.expect('auth_error')).error));
    await adminApi('unban', { name: 'bob' });
    banned.send({ type: 'login', name: 'bob', password: 'hemligt2' });
    await banned.expect('auth_ok');
    ok('avstängda konton kan inte logga in, och kan återställas');

    await adminApi('clear-reports', {});
    assert.strictEqual((await adminApi('state')).data.reports.length, 0);
    ok('rapporter kan rensas');

    console.log('Unity-appen');
    const u1 = await mk();
    const u2 = await mk();
    u1.send(unityMsg({ type: 'register', name: 'unityfan', password: 'hemligt1' }));
    u2.send(unityMsg({ type: 'register', name: 'webbfan', password: 'hemligt2' }));
    await u1.expect('auth_ok');
    await u2.expect('auth_ok');
    u1.send(unityMsg({ type: 'find', interests: ['musik', 'spel'] }));
    u2.send(unityMsg({ type: 'find', interests: ['musik'] }));
    const mu = await u1.expect('matched');
    assert.deepStrictEqual(mu.common, ['musik']);
    assert.strictEqual(mu.bot, false);
    u1.send(unityMsg({ type: 'typing', on: true }));
    assert.strictEqual((await u2.expect('typing')).on, true);
    u1.send(unityMsg({ type: 'chat', text: 'hej från unity' }));
    const fromUnity = await u2.expect('chat');
    assert.strictEqual(fromUnity.text, 'hej från unity');
    assert(Number.isInteger(fromUnity.id) && fromUnity.id >= 1);
    await u1.expect('chat'); // eget meddelande
    u1.send(unityMsg({ type: 'report', reason: 'Spam' }));
    assert(/sparats/.test((await u1.expect('info')).text));
    assert(fs.readFileSync(path.join(dataDir, 'reports.log'), 'utf8').includes('Orsak: Spam'));
    ok('meddelanden i Unitys format (alla fält med) fungerar hela vägen');

    console.log('Reaktioner');
    u2.send(unityMsg({ type: 'react', id: fromUnity.id, text: '♥' }));
    const r1 = await u1.expect('reaction');
    assert.deepStrictEqual([r1.id, r1.sender, r1.text], [fromUnity.id, 'webbfan', '♥']);
    await u2.expect('reaction');
    u2.send(unityMsg({ type: 'react', id: fromUnity.id, text: '★' }));
    assert.strictEqual((await u1.expect('reaction')).text, '★');
    u2.send(unityMsg({ type: 'react', id: fromUnity.id, text: '★' }));
    assert.strictEqual((await u1.expect('reaction')).text, '');
    ok('reaktion sätts, byts och tas bort (samma tecken igen)');

    u2.send(unityMsg({ type: 'react', id: fromUnity.id, text: '💩' }));
    u2.send(unityMsg({ type: 'react', id: 999, text: '♥' }));
    await u1.expectNone('reaction', 400);
    ok('okända tecken och meddelanden som inte finns ignoreras');

    console.log('Webbsidan');
    const home = await fetch(`http://127.0.0.1:${PORT}/`);
    assert(home.ok && (await home.text()).includes('<title>syncUp</title>'));
    assert((await fetch(`http://127.0.0.1:${PORT}/app.js`)).ok);
    assert.strictEqual((await fetch(`http://127.0.0.1:${PORT}/..%2Fusers.json`)).status, 404);
    assert.strictEqual((await fetch(`http://127.0.0.1:${PORT}/%E0`)).status, 404);
    assert((await fetch(`http://127.0.0.1:${PORT}/vendor/qrcode.js`)).ok);
    const { data: withUrls } = await adminApi('state');
    assert(Array.isArray(withUrls.joinUrls));
    ok('webbversionen och QR-biblioteket serveras, men inga filer utanför public/');

    console.log('Online-läge');
    const hostedPort = PORT + 1;
    const hosted = await startServer({
      PORT: String(hostedPort),
      DATA_DIR: fs.mkdtempSync(path.join(os.tmpdir(), 'syncup-hosted-')),
      HOSTED: '1',
      PUBLIC_URL: 'https://syncup-test.onrender.com/',
      ADMIN_KEY,
    });
    try {
      const res = await fetch(`http://127.0.0.1:${hostedPort}/admin/api/state`, { headers: { 'X-Admin-Key': ADMIN_KEY } });
      assert.deepStrictEqual((await res.json()).joinUrls, ['https://syncup-test.onrender.com']);
      const wrong = await fetch(`http://127.0.0.1:${hostedPort}/admin/api/state`, { headers: { 'X-Admin-Key': 'fel' } });
      assert.strictEqual(wrong.status, 401);
    } finally {
      hosted.kill();
    }
    ok('online visas den publika adressen, och nyckeln krävs fortfarande');

    const big = await mk();
    const closeCode = new Promise((resolve) => big.ws.addEventListener('close', (ev) => resolve(ev.code)));
    big.ws.send('x'.repeat(5000));
    assert.strictEqual(await closeCode, 1009);
    ok('för stora meddelanden stänger anslutningen (kod 1009)');
  } finally {
    sockets.forEach((s) => s.close());
    server.kill();
  }
  console.log('\nAlla tester gick igenom.');
}

main().catch((err) => {
  console.error('\nTEST MISSLYCKADES:', err.message);
  process.exit(1);
});
