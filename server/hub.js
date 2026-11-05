'use strict';

// Håller koll på alla som är anslutna, matchar ihop folk och skickar meddelanden.
//
// Meddelanden (JSON):
//   klient -> server: anon, register, login, find, next, bot, chat, typing, icebreaker, react, report
//   server -> klient: welcome, auth_ok, auth_error, searching, matched, chat, typing, reaction,
//                     icebreaker, partner_left, info, announce, kicked, online

const crypto = require('crypto');
const { Accounts } = require('./accounts');
const { INTERESTS, ICEBREAKERS, REACTIONS } = require('./content');
const { RateLimiter } = require('./security');
const bot = require('./bot');

const MAX_TEXT = 300;
const HISTORY_LIMIT = 30;          // sparas för rapporter
const REMATCH_AVOID_MS = 8000;     // matcha inte direkt med samma person igen
const FLOOD_WINDOW_MS = 5000;
const FLOOD_MAX = 12;
const TYPING_MIN_GAP_MS = 400;
const ICEBREAKER_COOLDOWN_MS = 4000;
const REPORT_COOLDOWN_MS = 10000;
const MAX_INTERESTS = 5;
const MAX_FAILED_LOGINS = 5;       // per anslutning
const IP_FAILED_LOGINS = 10;       // per IP och kvart
const IP_REGISTRATIONS = 5;        // per IP och timme

function createHub({ accounts, filterText, reports, interestWaitMs = 5000, botSpeed = 1, log = console.log }) {
  const clients = new Set();
  const onlineAccounts = new Set();
  const queues = { anon: [], account: [] };
  const rooms = new Set();
  const stats = { startedAt: Date.now(), matches: 0, messages: 0, reports: 0 };
  const failedLoginsByIp = new RateLimiter(IP_FAILED_LOGINS, 15 * 60 * 1000);
  const registrationsByIp = new RateLimiter(IP_REGISTRATIONS, 60 * 60 * 1000);

  const send = (ws, obj) => {
    if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
  };
  const info = (ws, text) => send(ws, { type: 'info', text });

  function broadcastOnline() {
    for (const c of clients) send(c, { type: 'online', online: clients.size });
  }

  function newState() {
    return {
      name: null,
      mode: null, // 'anon' eller 'account'
      interests: [],
      partner: null,
      room: null,
      lastRoom: null, // så att man kan rapportera efter att samtalet slutat
      lastPartner: null,
      avoidUntil: 0,
      queued: false,
      queuedAt: 0,
      connectedAt: Date.now(),
      msgTimes: [],
      lastWarn: 0,
      lastReport: 0,
      lastTyping: 0,
      failedLogins: 0,
    };
  }

  function anonName() {
    const taken = new Set([...clients].map((c) => c.s.name));
    let name;
    do {
      name = 'Anonym#' + (1000 + crypto.randomInt(9000));
    } while (taken.has(name));
    return name;
  }

  function findClient(name) {
    const key = String(name).toLowerCase();
    for (const c of clients) if (c.s.name.toLowerCase() === key) return c;
    return null;
  }

  function parseInterests(list) {
    if (!Array.isArray(list)) return [];
    const valid = list.filter((i) => INTERESTS.includes(i));
    return [...new Set(valid)].slice(0, MAX_INTERESTS);
  }

  // Matchning

  const avoids = (a, b, now) => a.s.lastPartner === b && now < a.s.avoidUntil;
  const shared = (a, b) => a.s.interests.filter((i) => b.s.interests.includes(i));

  // Har båda valt intressen måste de ha något gemensamt, tills någon av dem väntat för länge
  function canPair(a, b, now) {
    if (avoids(a, b, now) || avoids(b, a, now)) return false;
    if (!a.s.interests.length || !b.s.interests.length) return true;
    if (shared(a, b).length) return true;
    return now - a.s.queuedAt >= interestWaitMs || now - b.s.queuedAt >= interestWaitMs;
  }

  function dequeue(ws) {
    const q = queues[ws.s.mode];
    const i = q.indexOf(ws);
    if (i >= 0) q.splice(i, 1);
    ws.s.queued = false;
  }

  function enqueue(ws) {
    const s = ws.s;
    if (s.room) return;
    if (!s.queued) {
      queues[s.mode].push(ws);
      s.queued = true;
      s.queuedAt = Date.now();
    }
    send(ws, { type: 'searching' });
    tryMatch(s.mode);
  }

  // Den som väntat längst får den i kön som har flest gemensamma intressen
  function tryMatch(mode) {
    const q = queues[mode];
    const now = Date.now();
    for (let i = 0; i < q.length; i++) {
      let best = -1;
      let bestScore = -1;
      for (let j = i + 1; j < q.length; j++) {
        if (!canPair(q[i], q[j], now)) continue;
        const score = shared(q[i], q[j]).length;
        if (score > bestScore) {
          best = j;
          bestScore = score;
        }
      }
      if (best >= 0) {
        const a = q[i];
        const b = q[best];
        q.splice(best, 1);
        q.splice(i, 1);
        a.s.queued = b.s.queued = false;
        startRoom(a, b);
        return tryMatch(mode);
      }
    }
  }

  function startRoom(a, b) {
    const room = {
      mode: a.s.mode,
      members: [a, b],
      names: [a.s.name, b.s.name],
      history: [],
      startedAt: Date.now(),
      withBot: !!(a.isBot || b.isBot),
      lastIcebreakerAt: 0,
      lastQuestion: null,
      nextMessageId: 1,
      reactions: new Map(), // meddelande-id -> Map(namn -> tecken)
    };
    rooms.add(room);
    stats.matches++;
    a.s.room = b.s.room = room;
    a.s.lastRoom = b.s.lastRoom = room;
    a.s.partner = b;
    b.s.partner = a;
    log(`match (${room.mode}): ${a.s.name} <-> ${b.s.name}`);
    const common = shared(a, b);
    send(a, { type: 'matched', partner: b.s.name, common, bot: !!b.isBot });
    send(b, { type: 'matched', partner: a.s.name, common, bot: !!a.isBot });
  }

  function leaveRoom(ws) {
    const s = ws.s;
    const room = s.room;
    const p = s.partner;
    if (!room) return;
    rooms.delete(room);
    s.room = null;
    s.partner = null;
    if (p) {
      p.s.room = null;
      p.s.partner = null;
      s.lastPartner = p;
      p.s.lastPartner = ws;
      s.avoidUntil = p.s.avoidUntil = Date.now() + REMATCH_AVOID_MS;
      if (p.isBot) stopBot(p);
      else send(p, { type: 'partner_left' });
    }
  }

  function deliverChat(room, from, text) {
    const id = room.nextMessageId++;
    room.history.push({ t: Date.now(), from: from.s.name, text });
    if (room.history.length > HISTORY_LIMIT) room.history.shift();
    stats.messages++;
    const out = { type: 'chat', id, sender: from.s.name, text };
    for (const member of room.members) send(member, out);
  }

  // Samma tecken en gång till tar bort reaktionen
  function handleReact(ws, m) {
    const room = ws.s.room;
    const id = Number(m.id);
    if (!room || !Number.isInteger(id) || id < 1 || id >= room.nextMessageId) return;
    const emoji = String(m.text || '');
    if (!REACTIONS.includes(emoji)) return;

    if (!room.reactions.has(id)) room.reactions.set(id, new Map());
    const onMessage = room.reactions.get(id);
    const removed = onMessage.get(ws.s.name) === emoji;
    if (removed) onMessage.delete(ws.s.name);
    else onMessage.set(ws.s.name, emoji);

    const out = { type: 'reaction', id, sender: ws.s.name, text: removed ? '' : emoji };
    for (const member of room.members) send(member, out);
  }

  // syncBot är en låtsas-anslutning som får samma meddelanden som en vanlig klient

  function createBot(mode) {
    const botWs = { isBot: true, readyState: 1, s: newState(), lastAnswer: null, timers: new Set() };
    botWs.s.name = bot.BOT_NAME;
    botWs.s.mode = mode;
    botWs.send = (json) => onBotReceive(botWs, JSON.parse(json));
    return botWs;
  }

  function onBotReceive(botWs, m) {
    if (!botWs.s.room) return;
    if (m.type === 'matched') {
      botSay(botWs, bot.GREETING, 500 * botSpeed);
    } else if (m.type === 'chat' && m.sender !== botWs.s.name) {
      botWs.lastAnswer = bot.botReply(m.text, botWs.lastAnswer);
      botSay(botWs, botWs.lastAnswer);
    } else if (m.type === 'icebreaker') {
      botSay(botWs, bot.botIcebreakerReply());
    }
  }

  function botSay(botWs, text, delay = bot.typingDelay(text, botSpeed)) {
    const room = botWs.s.room;
    send(botWs.s.partner, { type: 'typing', on: true });
    const timer = setTimeout(() => {
      botWs.timers.delete(timer);
      if (botWs.s.room === room) deliverChat(room, botWs, text);
    }, delay);
    botWs.timers.add(timer);
  }

  function stopBot(botWs) {
    for (const t of botWs.timers) clearTimeout(t);
    botWs.timers.clear();
    botWs.readyState = 3;
  }

  function startBotChat(ws) {
    if (ws.s.room) return info(ws, 'Du chattar redan med någon.');
    dequeue(ws);
    startRoom(ws, createBot(ws.s.mode));
  }

  // Inloggning

  const authError = (ws, error) => send(ws, { type: 'auth_error', error });

  function join(ws, name, mode) {
    ws.s.name = name;
    ws.s.mode = mode;
    clients.add(ws);
    if (mode === 'account') onlineAccounts.add(name.toLowerCase());
    send(ws, { type: mode === 'anon' ? 'welcome' : 'auth_ok', name, mode });
    broadcastOnline();
  }

  function handleAuth(ws, m) {
    if (m.type === 'anon') return join(ws, anonName(), 'anon');
    if (m.type !== 'register' && m.type !== 'login') return;

    let name = String(m.name || '').trim();
    const password = String(m.password || '');

    const nameError = Accounts.validateName(name);
    if (nameError) return authError(ws, nameError);

    const ip = ws.s.ip;
    if (m.type === 'register') {
      if (password.length < 6 || password.length > 64) {
        return authError(ws, 'Lösenordet måste vara 6–64 tecken.');
      }
      if (registrationsByIp.blocked(ip)) {
        return authError(ws, 'För många nya konton härifrån. Försök igen om en stund.');
      }
      const err = accounts.register(name, password);
      if (err) return authError(ws, err);
      registrationsByIp.hit(ip);
    } else {
      if (failedLoginsByIp.blocked(ip)) {
        return authError(ws, 'För många felaktiga inloggningar. Vänta en kvart och försök igen.');
      }
      const user = accounts.check(name, password);
      if (!user) {
        ws.s.failedLogins++;
        failedLoginsByIp.hit(ip);
        authError(ws, 'Fel användarnamn eller lösenord.');
        if (ws.s.failedLogins >= MAX_FAILED_LOGINS) ws.close();
        return;
      }
      if (user.banned) return authError(ws, 'Kontot är avstängt av admin.');
      name = user.name;
    }

    if (onlineAccounts.has(name.toLowerCase())) {
      return authError(ws, 'Kontot är redan inloggat på en annan enhet.');
    }
    join(ws, name, 'account');
  }

  // Meddelanden

  function allowMessage(ws, now) {
    const s = ws.s;
    s.msgTimes = s.msgTimes.filter((t) => now - t < FLOOD_WINDOW_MS);
    if (s.msgTimes.length >= FLOOD_MAX) {
      if (now - s.lastWarn > FLOOD_WINDOW_MS) {
        s.lastWarn = now;
        info(ws, 'Lugna ner dig, du skriver för snabbt.');
      }
      return false;
    }
    s.msgTimes.push(now);
    return true;
  }

  function handleChat(ws, m) {
    const s = ws.s;
    if (!s.partner || !s.room) return info(ws, 'Du har ingen att chatta med just nu.');
    const original = String(m.text || '').slice(0, MAX_TEXT).trim();
    if (!original) return;
    const text = filterText(original);
    if (!text) return;
    if (text !== original.replace(/\s+/g, ' ')) {
      info(ws, 'Delar av ditt meddelande doldes (grova ord eller kontaktuppgifter).');
    }
    deliverChat(s.room, ws, text);
  }

  function handleTyping(ws, m, now) {
    const s = ws.s;
    if (!s.partner) return;
    const on = !!m.on;
    if (on && now - s.lastTyping < TYPING_MIN_GAP_MS) return;
    s.lastTyping = on ? now : 0;
    send(s.partner, { type: 'typing', on });
  }

  function handleIcebreaker(ws, now) {
    const room = ws.s.room;
    if (!room) return info(ws, 'Du har ingen att chatta med just nu.');
    if (now - room.lastIcebreakerAt < ICEBREAKER_COOLDOWN_MS) {
      return info(ws, 'Vänta lite innan nästa isbrytare.');
    }
    const options = ICEBREAKERS.filter((q) => q !== room.lastQuestion);
    const text = options[crypto.randomInt(options.length)];
    room.lastIcebreakerAt = now;
    room.lastQuestion = text;
    room.history.push({ t: now, from: 'Isbrytare', text });
    const out = { type: 'icebreaker', sender: ws.s.name, text };
    for (const member of room.members) send(member, out);
  }

  function handleReport(ws, m, now) {
    const s = ws.s;
    const room = s.room || s.lastRoom;
    if (!room) return info(ws, 'Det finns ingen att rapportera.');
    if (now - s.lastReport < REPORT_COOLDOWN_MS) return info(ws, 'Du har just rapporterat. Vänta några sekunder.');
    const idx = room.members.indexOf(ws);
    if (idx < 0) return;
    s.lastReport = now;
    const reported = room.names[1 - idx];
    const report = {
      reporter: s.name,
      reporterMode: s.mode,
      reported,
      reason: String(m.reason || '').slice(0, 200),
      history: room.history,
    };
    reports.write(report, (err) => {
      if (err) {
        log('Kunde inte skriva rapport: ' + err.message);
        return info(ws, 'Kunde inte spara rapporten.');
      }
      stats.reports++;
      log(`rapport: ${s.name} rapporterade ${reported}`);
      info(ws, 'Tack! Rapporten har sparats.');
    });
  }

  function onMessage(ws, raw) {
    let m;
    try {
      m = JSON.parse(raw.toString());
    } catch {
      return;
    }
    if (!m || typeof m.type !== 'string') return;

    const s = ws.s;
    const now = Date.now();

    // typing har en egen spärr och räknas inte i flodskyddet
    if (m.type === 'typing') return s.name ? handleTyping(ws, m, now) : undefined;
    if (!allowMessage(ws, now)) return;
    if (!s.name) return handleAuth(ws, m);

    switch (m.type) {
      case 'find':
        if (s.room) return info(ws, 'Du chattar redan med någon.');
        s.interests = parseInterests(m.interests);
        return enqueue(ws);
      case 'next':
        leaveRoom(ws);
        return enqueue(ws);
      case 'bot':
        return startBotChat(ws);
      case 'chat':
        return handleChat(ws, m);
      case 'icebreaker':
        return handleIcebreaker(ws, now);
      case 'react':
        return handleReact(ws, m);
      case 'report':
        return handleReport(ws, m, now);
      default:
        return;
    }
  }

  function onClose(ws) {
    const s = ws.s;
    if (!s.name || !clients.has(ws)) return;
    dequeue(ws);
    leaveRoom(ws);
    clients.delete(ws);
    if (s.mode === 'account') onlineAccounts.delete(s.name.toLowerCase());
    log(`frånkopplad: ${s.name}`);
    broadcastOnline();
  }

  function attach(ws, ip = 'okänd') {
    ws.s = newState();
    ws.s.ip = ip;
    ws.on('message', (data) => onMessage(ws, data));
    ws.on('close', () => onClose(ws));
  }

  // Körs varje sekund så att de som väntat klart på intressen eller samma person blir matchade
  function tick() {
    tryMatch('anon');
    tryMatch('account');
  }

  // Admin

  function statusOf(ws) {
    if (ws.s.room) return 'chattar';
    if (ws.s.queued) return 'söker';
    return 'ledig';
  }

  // Skickar aldrig med vad folk skriver
  function snapshot() {
    const now = Date.now();
    const list = [...clients];
    return {
      uptimeSec: Math.round((now - stats.startedAt) / 1000),
      online: clients.size,
      anon: list.filter((c) => c.s.mode === 'anon').length,
      account: list.filter((c) => c.s.mode === 'account').length,
      queued: queues.anon.length + queues.account.length,
      stats: { matches: stats.matches, messages: stats.messages, reports: stats.reports },
      rooms: [...rooms].map((r) => ({
        names: r.names,
        mode: r.mode,
        withBot: r.withBot,
        seconds: Math.round((now - r.startedAt) / 1000),
        messages: r.history.length,
      })),
      connected: list.map((c) => ({
        name: c.s.name,
        mode: c.s.mode,
        status: statusOf(c),
        partner: c.s.partner ? c.s.partner.s.name : null,
        interests: c.s.interests,
        seconds: Math.round((now - c.s.connectedAt) / 1000),
      })),
      onlineAccounts: [...onlineAccounts],
    };
  }

  function kick(name, reason = 'Du har kopplats bort av admin.') {
    const ws = findClient(name);
    if (!ws) return false;
    send(ws, { type: 'kicked', text: reason });
    log(`admin: kickade ${ws.s.name}`);
    ws.close();
    return true;
  }

  function announce(text) {
    for (const c of clients) send(c, { type: 'announce', text });
    log(`admin: meddelande till alla: ${text}`);
    return clients.size;
  }

  return {
    attach,
    tick,
    snapshot,
    kick,
    announce,
    get onlineCount() {
      return clients.size;
    },
  };
}

module.exports = { createHub, MAX_TEXT };
