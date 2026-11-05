'use strict';

// syncUp-servern. Kör med "Starta syncUp.bat" eller npm start.
// Inställningar: PORT, DATA_DIR, ADMIN_KEY, HOSTED, PUBLIC_URL, INTEREST_WAIT_MS, BOT_SPEED, LOG_FILE

const http = require('http');
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const { WebSocketServer } = require('./miniws');
const { Accounts } = require('./accounts');
const { createFilter } = require('./filter');
const { Reports } = require('./reports');
const { createHub } = require('./hub');
const { createAdmin } = require('./admin');
const { ConnectionCounter, clientIp, securityHeaders } = require('./security');

// Unity startar servern utan fönster och läser loggen från den här filen i stället
// Skrivs direkt (sync), annars hinner raderna inte sparas när Unity stänger servern
if (process.env.LOG_FILE) {
  const logFile = process.env.LOG_FILE;
  fs.writeFileSync(logFile, '');
  for (const level of ['log', 'error']) {
    const original = console[level];
    console[level] = (...args) => {
      fs.appendFileSync(logFile, args.join(' ') + '\n');
      original(...args);
    };
  }
}

const PORT = Number(process.env.PORT) || 8080;
const DATA_DIR = process.env.DATA_DIR || __dirname;
// Render sätter RENDER och RENDER_EXTERNAL_URL
const HOSTED = process.env.HOSTED === '1' || !!process.env.RENDER;
const PUBLIC_URL = (process.env.PUBLIC_URL || process.env.RENDER_EXTERNAL_URL || '').replace(/\/$/, '');
// Längre nyckel online eftersom adminpanelen då går att nå från internet
const ADMIN_KEY = process.env.ADMIN_KEY || crypto.randomBytes(HOSTED ? 16 : 6).toString('hex');
const PUBLIC_DIR = path.join(__dirname, 'public');
const PING_INTERVAL_MS = 30000;
const MATCH_INTERVAL_MS = 1000;
const MAX_CONNECTIONS_PER_IP = Number(process.env.MAX_CONNECTIONS_PER_IP) || 20;

const accounts = new Accounts(path.join(DATA_DIR, 'users.json'));
const filter = createFilter(path.join(__dirname, 'badwords.txt'));
const reports = new Reports(path.join(DATA_DIR, 'reports.log'));
const hub = createHub({
  accounts,
  reports,
  filterText: filter.filterText,
  interestWaitMs: Number(process.env.INTEREST_WAIT_MS) || 5000,
  botSpeed: Number(process.env.BOT_SPEED) || 1,
});
const admin = createAdmin({ hub, accounts, reports, key: ADMIN_KEY, allowRemote: HOSTED, joinUrls });

// Adresserna som visas som QR-kod i adminpanelen
function joinUrls() {
  if (PUBLIC_URL) return [PUBLIC_URL];
  return lanAddresses().map((ip) => `http://${ip}:${PORT}`);
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

function serveStatic(res, pathname) {
  const file = path.normalize(path.join(PUBLIC_DIR, pathname === '/' ? 'index.html' : pathname));
  if (!file.startsWith(PUBLIC_DIR + path.sep)) return notFound(res); // inga ../
  fs.stat(file, (err, stat) => {
    if (err || !stat.isFile()) return notFound(res);
    res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' });
    fs.createReadStream(file).pipe(res);
  });
}

function notFound(res) {
  res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
  res.end('Sidan finns inte.\n');
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const headers = securityHeaders(url.pathname === '/admin' || url.pathname.startsWith('/admin/'));
  for (const [name, value] of Object.entries(headers)) res.setHeader(name, value);
  if (admin.handle(req, res, url)) return;
  if (url.pathname === '/status') {
    res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
    return res.end(`syncUp-servern körs. Online just nu: ${hub.onlineCount}\n`);
  }
  if (req.method !== 'GET' && req.method !== 'HEAD') return notFound(res);
  let pathname;
  try {
    pathname = decodeURIComponent(url.pathname);
  } catch {
    return notFound(res);
  }
  serveStatic(res, pathname);
});

const wss = new WebSocketServer({ server, maxPayload: 4096 });
const connections = new ConnectionCounter(MAX_CONNECTIONS_PER_IP);

wss.on('connection', (ws, req) => {
  const ip = clientIp(req, HOSTED);
  if (!connections.add(ip)) {
    ws.close(1008);
    return;
  }
  ws.on('close', () => connections.remove(ip));
  ws.isAlive = true;
  ws.on('pong', () => {
    ws.isAlive = true;
  });
  ws.on('error', () => {});
  hub.attach(ws, ip);
});

// Stänger anslutningar som slutat svara
setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) {
      ws.terminate();
      continue;
    }
    ws.isAlive = false;
    ws.ping();
  }
}, PING_INTERVAL_MS);

setInterval(hub.tick, MATCH_INTERVAL_MS);

function lanAddresses() {
  return Object.values(os.networkInterfaces())
    .flat()
    .filter((a) => a && a.family === 'IPv4' && !a.internal)
    .map((a) => a.address);
}

server.listen(PORT, () => {
  console.log(`syncUp-servern lyssnar på port ${PORT}${HOSTED ? ' (online)' : ''}`);
  console.log(`Konton: ${accounts.count} st, ordfilter: ${filter.wordCount} ord`);
  console.log('');
  if (HOSTED) {
    const base = PUBLIC_URL || '(din Render-adress)';
    console.log(`Chatt:      ${base}`);
    console.log(`Unity:      ${base.replace(/^http/, 'ws')}`);
    console.log(`Adminpanel: ${base}/admin?key=${encodeURIComponent(ADMIN_KEY)}`);
    return;
  }
  console.log('Anslut från den här datorn:');
  console.log(`  Webb:  http://localhost:${PORT}`);
  console.log(`  Unity: ws://localhost:${PORT}`);
  const lan = lanAddresses();
  if (lan.length) {
    console.log('Anslut från andra datorer/mobiler i samma nätverk:');
    for (const ip of lan) console.log(`  Webb:  http://${ip}:${PORT}    Unity: ws://${ip}:${PORT}`);
  }
  console.log('');
  const adminUrl = `http://localhost:${PORT}/admin?key=${encodeURIComponent(ADMIN_KEY)}`;
  console.log(`Adminpanel (bara på den här datorn): ${adminUrl}`);
  if (process.env.OPEN_BROWSER === '1') {
    openInBrowser(`http://localhost:${PORT}`);
    openInBrowser(adminUrl);
  }
});

// Om servern redan körs (t.ex. i ett annat fönster) öppnas bara chatten
server.on('error', (err) => {
  if (err.code !== 'EADDRINUSE') throw err;
  console.log(`Port ${PORT} används redan. Servern körs nog redan i ett annat fönster,`);
  console.log('adminlänken står i det fönstret.');
  if (process.env.OPEN_BROWSER === '1') openInBrowser(`http://localhost:${PORT}`, () => process.exit(1));
  else process.exit(1);
});

function openInBrowser(url, done) {
  const { exec } = require('child_process');
  const command =
    process.platform === 'win32' ? `start "" "${url}"` : process.platform === 'darwin' ? `open "${url}"` : `xdg-open "${url}"`;
  exec(command, (err) => {
    if (err) console.log('Kunde inte öppna webbläsaren: ' + err.message);
    if (done) done();
  });
}

process.on('SIGINT', () => {
  console.log('\nStänger ner...');
  process.exit(0);
});
