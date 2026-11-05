'use strict';

/*
 * syncUp – webbversionen av chatten.
 * Pratar samma protokoll som Unity-appen, så webb- och Unity-användare kan chatta med varandra.
 */

const INTERESTS = [
  ['spel', 'Spel'], ['musik', 'Musik'], ['sport', 'Sport'], ['film', 'Film & serier'], ['bocker', 'Böcker'],
  ['mat', 'Mat'], ['djur', 'Djur'], ['resor', 'Resor'], ['teknik', 'Teknik'], ['konst', 'Konst'],
];
const INTEREST_LABEL = Object.fromEntries(INTERESTS);
const MAX_INTERESTS = 5;
const REPORT_REASONS = ['Spam', 'Elakt språk', 'Olämpligt innehåll', 'Annat'];
const REACTIONS = ['♥', '★', '☺']; // samma som servern (content.js)
const TYPING_RESEND_MS = 2500;   // skicka "skriver" igen så här ofta medan man skriver
const TYPING_IDLE_MS = 3000;     // sluta visa "skriver" när man varit still så här länge
const TYPING_SHOW_MS = 6000;     // dölj partnerns "skriver" om inget hörts på så här länge
const MAX_ROWS = 200;

const $ = (id) => document.getElementById(id);

const state = {
  ws: null,
  entry: 'home',          // skärmen vi kom från, dit går vi tillbaka vid fel
  pendingHello: null,     // första meddelandet som skickas när anslutningen är uppe
  kickedReason: null,
  myName: null,
  partner: null,
  lastPartner: null,     // den senaste man chattat med, går att rapportera även efter att den lämnat
  partnerIsBot: false,
  online: 0,
  canReport: false,
  interests: new Set(),
  typingSentAt: 0,
  typingIdleTimer: null,
  partnerTypingTimer: null,
  reactions: new Map(),   // meddelande-id -> Map(namn -> tecken)
};

// ---------------------------------------------------------------- Skärmar

function show(screen) {
  for (const id of ['home', 'auth', 'chat']) $(id).classList.toggle('hidden', id !== screen);
  if (screen === 'chat') $('chatInput').focus();
}

function setEntryStatus(text) {
  $('homeStatus').textContent = state.entry === 'home' ? text : '';
  $('authStatus').textContent = state.entry === 'auth' ? text : '';
}

function buildInterests() {
  for (const [id, label] of INTERESTS) {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.addEventListener('change', () => {
      if (box.checked && state.interests.size >= MAX_INTERESTS) box.checked = false;
      else if (box.checked) state.interests.add(id);
      else state.interests.delete(id);
    });
    const chip = document.createElement('label');
    chip.append(box, text('span', label));
    $('interests').appendChild(chip);
  }
}

function buildReasons() {
  for (const reason of REPORT_REASONS) {
    const b = document.createElement('button');
    b.textContent = reason;
    b.addEventListener('click', () => {
      send({ type: 'report', reason });
      $('reportModal').classList.add('hidden');
    });
    $('reasons').appendChild(b);
  }
}

// ---------------------------------------------------------------- Nätverk

function serverUrl() {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return proto + '//' + (location.host || 'localhost:8080');
}

function send(obj) {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify(obj));
}

function connect(hello, entry) {
  closeSocket();
  state.entry = entry;
  state.pendingHello = hello;
  state.kickedReason = null;
  setEntryStatus('Ansluter...');

  const ws = new WebSocket(serverUrl());
  state.ws = ws;
  ws.addEventListener('open', () => {
    send(state.pendingHello);
    state.pendingHello = null;
  });
  ws.addEventListener('message', (ev) => {
    let m;
    try {
      m = JSON.parse(ev.data);
    } catch {
      return; // trasigt meddelande, ignorera
    }
    if (m && typeof m.type === 'string') handleMessage(m);
  });
  ws.addEventListener('close', () => {
    if (state.ws !== ws) return; // en gammal anslutning som vi själva stängt
    state.ws = null;
    const reason = state.kickedReason || 'Anslutningen bröts.';
    resetSession();
    show(state.entry);
    setEntryStatus(reason);
  });
}

/** Stänger anslutningen utan att visa något felmeddelande. */
function closeSocket() {
  const ws = state.ws;
  state.ws = null;
  if (ws) ws.close();
}

function handleMessage(m) {
  switch (m.type) {
    case 'welcome':
    case 'auth_ok':
      state.myName = m.name;
      clearChat();
      show('chat');
      addSystem('Du är ' + m.name + '.');
      send({ type: 'find', interests: [...state.interests] });
      refreshUi();
      break;

    case 'auth_error':
      closeSocket();
      show('auth');
      $('authStatus').textContent = m.error;
      break;

    case 'searching':
      state.partner = null;
      state.canReport = false;
      refreshUi();
      addSystem('Söker...');
      break;

    case 'matched':
      state.partner = m.partner;
      state.lastPartner = m.partner;
      state.partnerIsBot = !!m.bot;
      state.canReport = !m.bot;
      clearChat();
      addSystem('Ansluten till ' + m.partner + (m.bot ? ' (testbot)' : '') + '.');
      if (m.common && m.common.length) {
        addSystem('Gemensamt: ' + m.common.map((i) => INTEREST_LABEL[i] || i).join(', ').toLowerCase() + '.');
      }
      refreshUi();
      $('chatInput').focus();
      break;

    case 'chat':
      if (m.sender !== state.myName) {
        setPartnerTyping(false);
        notify();
      }
      addChat(m.sender, m.text, m.id);
      break;

    case 'reaction':
      setReaction(m.id, m.sender, m.text);
      break;

    case 'typing':
      setPartnerTyping(m.on);
      break;

    case 'icebreaker':
      addIcebreaker(m.sender, m.text);
      break;

    case 'partner_left':
      addSystem(state.partner + ' lämnade.');
      state.partner = null;
      setPartnerTyping(false);
      refreshUi();
      break;

    case 'info':
      addSystem(m.text);
      break;

    case 'announce':
      addAnnounce(m.text);
      notify();
      break;

    case 'kicked':
      state.kickedReason = m.text;
      break;

    case 'online':
      state.online = m.online;
      refreshUi();
      break;
  }
}

function resetSession() {
  state.partner = null;
  state.partnerIsBot = false;
  state.myName = null;
  state.online = 0;
  state.canReport = false;
  setPartnerTyping(false);
  clearChat();
  refreshUi();
}

function refreshUi() {
  const has = state.partner !== null;
  $('chatName').textContent = has ? state.partner : 'Söker...';
  $('chatMeta').textContent = [state.partnerIsBot && has ? 'testbot' : '', state.online > 0 ? state.online + ' online' : '']
    .filter(Boolean).join('  ·  ');
  setAvatar(has ? state.partner : null);
  $('chatInput').disabled = !has;
  $('sendBtn').disabled = !has;
  $('iceBtn').disabled = !has;
  $('reportBtn').disabled = !state.canReport;
  $('botBtn').classList.toggle('hidden', has);
}

// ---------------------------------------------------------------- "Skriver..."

function onTyping() {
  if (!state.partner) return;
  const now = Date.now();
  if (now - state.typingSentAt > TYPING_RESEND_MS) {
    state.typingSentAt = now;
    send({ type: 'typing', on: true });
  }
  clearTimeout(state.typingIdleTimer);
  state.typingIdleTimer = setTimeout(stopTyping, TYPING_IDLE_MS);
}

function stopTyping() {
  clearTimeout(state.typingIdleTimer);
  if (!state.typingSentAt) return;
  state.typingSentAt = 0;
  send({ type: 'typing', on: false });
}

function setPartnerTyping(on) {
  clearTimeout(state.partnerTypingTimer);
  $('typing').textContent = on && state.partner ? state.partner + ' skriver...' : '';
  if (on) state.partnerTypingTimer = setTimeout(() => setPartnerTyping(false), TYPING_SHOW_MS);
}

// ---------------------------------------------------------------- Ljud

let audio = null;

/** Ett kort "pling" när ett meddelande kommer och fliken inte är i fokus. */
function notify() {
  if (document.hasFocus()) return;
  try {
    audio = audio || new AudioContext();
    const osc = audio.createOscillator();
    const gain = audio.createGain();
    osc.frequency.value = 880;
    gain.gain.setValueAtTime(0.15, audio.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, audio.currentTime + 0.25);
    osc.connect(gain).connect(audio.destination);
    osc.start();
    osc.stop(audio.currentTime + 0.25);
  } catch {
    /* ljud stöds inte, inget att göra */
  }
}

// ---------------------------------------------------------------- Meddelandelistan

const time = () => new Date().toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit' });

function text(tag, value, className) {
  const node = document.createElement(tag);
  node.textContent = value; // textContent, aldrig innerHTML, så ingen kan skicka in egen HTML
  if (className) node.className = className;
  return node;
}

// Dämpade färger för avatarerna, samma namn får alltid samma färg
const AVATAR_COLORS = ['#5b6ee1', '#3f9e8f', '#c0703f', '#9a5bc4', '#c2577a', '#5f8f3e'];

function setAvatar(name) {
  const avatar = $('avatar');
  if (!name) {
    avatar.textContent = '?';
    avatar.style.background = '';
    return;
  }
  let hash = 0;
  for (const ch of name) hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
  avatar.textContent = name[0].toUpperCase();
  avatar.style.background = AVATAR_COLORS[hash % AVATAR_COLORS.length];
}

/** Lägger till något i listan och scrollar ner om man redan var längst ner (eller om man själv skrev). */
function addRow(node, forceScroll = false) {
  const list = $('messages');
  const atBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 80;
  list.appendChild(node);
  while (list.children.length > MAX_ROWS) list.firstChild.remove();
  if (atBottom || forceScroll) list.scrollTop = list.scrollHeight;
}

function addChat(sender, value, id) {
  const mine = sender === state.myName;
  const last = $('messages').lastElementChild;
  const row = document.createElement('div');
  // "first" = första bubblan i en följd från samma person, får lite luft ovanför
  const sameSender = last && last.classList.contains('row') && last.dataset.sender === sender;
  row.className = 'row ' + (mine ? 'me' : 'them') + (sameSender ? '' : ' first');
  row.dataset.sender = sender;
  const bubble = text('div', value, 'bubble');
  if (id) {
    row.dataset.id = id;
    bubble.title = 'Klicka för att reagera';
    bubble.addEventListener('click', () => togglePicker(row));
  }
  row.append(bubble, text('span', time(), 'time'));
  addRow(row, mine);
}

// ---------------------------------------------------------------- Reaktioner

/** Visar (eller döljer) en liten rad med reaktioner precis under bubblan. */
function togglePicker(row) {
  const open = $('messages').querySelector('.picker');
  const wasThisRow = open && open.previousElementSibling === row;
  if (open) open.remove();
  if (wasThisRow) return;

  const picker = document.createElement('div');
  picker.className = 'picker ' + (row.classList.contains('me') ? 'me' : 'them');
  for (const emoji of REACTIONS) {
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = emoji;
    b.addEventListener('click', () => {
      send({ type: 'react', id: Number(row.dataset.id), text: emoji });
      picker.remove();
    });
    picker.appendChild(b);
  }
  row.after(picker);
}

/** Uppdaterar en reaktion och ritar om märket på bubblan, t.ex. "♥ 2  ★". */
function setReaction(id, sender, emoji) {
  if (!state.reactions.has(id)) state.reactions.set(id, new Map());
  const onMessage = state.reactions.get(id);
  if (emoji) onMessage.set(sender, emoji);
  else onMessage.delete(sender);

  const row = $('messages').querySelector('.row[data-id="' + id + '"]');
  if (!row) return;
  const counts = new Map();
  for (const e of onMessage.values()) counts.set(e, (counts.get(e) || 0) + 1);
  const label = [...counts].map(([e, n]) => (n > 1 ? e + ' ' + n : e)).join('  ');

  let badge = row.querySelector('.reacts');
  if (!label) {
    if (badge) badge.remove();
    row.classList.remove('reacted');
    return;
  }
  if (!badge) {
    badge = text('span', '', 'reacts');
    row.querySelector('.bubble').appendChild(badge);
  }
  badge.textContent = label;
  row.classList.add('reacted');
}

function addSystem(value) {
  addRow(text('div', value, 'system'));
}

function addNotice(title, value) {
  const box = document.createElement('div');
  box.className = 'notice';
  box.append(text('b', title), value);
  addRow(box);
}

function addIcebreaker(sender, value) {
  addNotice('Fråga från ' + (sender === state.myName ? 'dig' : sender), value);
}

function addAnnounce(value) {
  addNotice('Meddelande från admin', value);
}

function clearChat() {
  $('messages').replaceChildren();
  state.reactions.clear();
}

// ---------------------------------------------------------------- Knappar

$('anonBtn').addEventListener('click', () => connect({ type: 'anon' }, 'home'));

$('openAuthBtn').addEventListener('click', () => {
  $('authStatus').textContent = '';
  show('auth');
  $('userInput').focus();
});

$('authBackBtn').addEventListener('click', () => show('home'));

$('authForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const type = e.submitter && e.submitter.dataset.auth === 'register' ? 'register' : 'login';
  const name = $('userInput').value.trim();
  const password = $('passInput').value;
  if (name.length < 3) {
    $('authStatus').textContent = 'Skriv ett användarnamn med minst 3 tecken.';
    return;
  }
  if (!password || (type === 'register' && password.length < 6)) {
    $('authStatus').textContent = type === 'register' ? 'Lösenordet måste vara minst 6 tecken.' : 'Skriv ditt lösenord.';
    return;
  }
  connect({ type, name, password }, 'auth');
});

$('sendForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const value = $('chatInput').value.trim();
  if (!value || !state.partner) return;
  send({ type: 'chat', text: value });
  stopTyping();
  $('chatInput').value = '';
  $('chatInput').focus();
});

$('chatInput').addEventListener('input', onTyping);

$('nextBtn').addEventListener('click', () => {
  clearChat();
  stopTyping();
  setPartnerTyping(false);
  state.canReport = false;
  send({ type: 'next' });
});

$('botBtn').addEventListener('click', () => send({ type: 'bot' }));
$('iceBtn').addEventListener('click', () => send({ type: 'icebreaker' }));

$('reportBtn').addEventListener('click', () => {
  $('reportName').textContent = state.lastPartner || '';
  $('reportModal').classList.remove('hidden');
});
$('reportCancel').addEventListener('click', () => $('reportModal').classList.add('hidden'));

$('leaveBtn').addEventListener('click', () => {
  closeSocket();
  resetSession();
  setEntryStatus('');
  show('home');
});

buildInterests();
buildReasons();
refreshUi();
