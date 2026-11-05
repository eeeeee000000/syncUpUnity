'use strict';

/*
 * Adminpanelen: /admin i webbläsaren.
 *
 * Skydd:
 *   - Lokalt: bara anrop från samma dator som servern körs på släpps igenom (inte från andra i nätverket).
 *   - Online (allowRemote): panelen nås från internet, så då skyddar bara nyckeln. Använd en lång nyckel.
 *   - Alltid: man måste ha adminnyckeln, som skrivs ut när servern startar (eller sätts med ADMIN_KEY).
 */

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const PAGE_FILE = path.join(__dirname, 'admin.html');
const MAX_BODY = 4096;
const LOCAL_ADDRESSES = new Set(['127.0.0.1', '::1', '::ffff:127.0.0.1']);

function safeEqual(a, b) {
  const x = Buffer.from(String(a));
  const y = Buffer.from(String(b));
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}

function json(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(body));
}

function readJson(req) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        reject(new Error('För stor förfrågan.'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      try {
        resolve(chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : {});
      } catch {
        reject(new Error('Ogiltig JSON.'));
      }
    });
    req.on('error', reject);
  });
}

/**
 * @param {object} opts
 * @param {boolean} [opts.allowRemote] släpp in anrop från andra datorer (när servern körs online)
 * @param {() => string[]} [opts.joinUrls] adresserna man går till för att chatta, visas som QR-kod
 */
function createAdmin({ hub, accounts, reports, key, allowRemote = false, joinUrls = () => [] }) {
  // POST-åtgärder: tar emot JSON-kroppen och svarar med { ok } eller kastar ett fel med ett svenskt meddelande
  const actions = {
    kick({ name }) {
      if (!hub.kick(name)) throw new Error('Ingen med det namnet är online.');
      return { ok: true };
    },
    ban({ name }) {
      if (!accounts.setBanned(name, true)) throw new Error('Kontot finns inte.');
      hub.kick(name, 'Ditt konto har stängts av av admin.');
      return { ok: true };
    },
    unban({ name }) {
      if (!accounts.setBanned(name, false)) throw new Error('Kontot finns inte.');
      return { ok: true };
    },
    announce({ text }) {
      const message = String(text || '').trim().slice(0, 200);
      if (!message) throw new Error('Skriv ett meddelande först.');
      return { ok: true, sentTo: hub.announce(message) };
    },
    'clear-reports'() {
      reports.clear();
      return { ok: true };
    },
  };

  function state() {
    const snap = hub.snapshot();
    const online = new Set(snap.onlineAccounts);
    return {
      ...snap,
      accounts: accounts.list().map((a) => ({ ...a, online: online.has(a.name.toLowerCase()) })),
      reports: reports.read().slice(0, 50),
      joinUrls: joinUrls(),
    };
  }

  /** Hanterar alla /admin-adresser. Returnerar false om adressen inte hör till adminpanelen. */
  function handle(req, res, url) {
    if (url.pathname !== '/admin' && !url.pathname.startsWith('/admin/')) return false;

    if (!allowRemote && !LOCAL_ADDRESSES.has(req.socket.remoteAddress)) {
      json(res, 403, { error: 'Adminpanelen kan bara öppnas på datorn där servern körs.' });
      return true;
    }

    const given = req.headers['x-admin-key'] || url.searchParams.get('key') || '';
    if (!safeEqual(given, key)) {
      if (url.pathname === '/admin') {
        res.writeHead(401, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('Fel eller saknad adminnyckel. Använd länken som skrivs ut när servern startar.\n');
      } else {
        json(res, 401, { error: 'Fel adminnyckel.' });
      }
      return true;
    }

    if (req.method === 'GET' && url.pathname === '/admin') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
      fs.createReadStream(PAGE_FILE).pipe(res);
      return true;
    }

    if (req.method === 'GET' && url.pathname === '/admin/api/state') {
      json(res, 200, state());
      return true;
    }

    const action = url.pathname.startsWith('/admin/api/') && actions[url.pathname.slice('/admin/api/'.length)];
    if (req.method === 'POST' && action) {
      readJson(req)
        .then((body) => json(res, 200, action(body)))
        .catch((err) => json(res, 400, { error: err.message }));
      return true;
    }

    json(res, 404, { error: 'Finns inte.' });
    return true;
  }

  return { handle };
}

module.exports = { createAdmin };
