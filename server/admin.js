'use strict';

// Adminpanelen på /admin. Kräver nyckeln, och lokalt går den bara att öppna på serverdatorn.

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

function createAdmin({ hub, accounts, reports, key, allowRemote = false, joinUrls = () => [] }) {
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

  // Returnerar false om adressen inte är en /admin-adress
  function handle(req, res, url) {
    if (url.pathname !== '/admin' && !url.pathname.startsWith('/admin/')) return false;

    if (!allowRemote && !LOCAL_ADDRESSES.has(req.socket.remoteAddress)) {
      json(res, 403, { error: 'Adminpanelen kan bara öppnas på datorn där servern körs.' });
      return true;
    }

    // Sidan i sig är inte hemlig, det är API:t som kräver nyckeln
    if (req.method === 'GET' && url.pathname === '/admin') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
      fs.createReadStream(PAGE_FILE).pipe(res);
      return true;
    }

    const given = req.headers['x-admin-key'] || '';
    if (!safeEqual(given, key)) {
      json(res, 401, { error: 'Fel adminnyckel. Använd länken som skrivs ut när servern startar.' });
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
