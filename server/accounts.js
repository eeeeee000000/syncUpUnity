'use strict';

// Konton: sparas i users.json, lösenord som scrypt-hash med eget salt per konto.

const fs = require('fs');
const crypto = require('crypto');

const NAME_RE = /^[A-Za-z0-9_åäöÅÄÖ]{3,16}$/;
const RESERVED_RE = /^(anonym|syncbot|admin)/i;

class Accounts {
  constructor(file) {
    this.file = file;
    this.users = new Map(); // gemener-namn -> { name, salt, hash, created, banned? }
    try {
      const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
      for (const [key, value] of Object.entries(saved)) this.users.set(key, value);
    } catch {
      /* ingen användarfil ännu */
    }
  }

  get count() {
    return this.users.size;
  }

  /** Returnerar ett felmeddelande om namnet inte får användas, annars null. */
  static validateName(name) {
    if (!NAME_RE.test(name)) return 'Användarnamn: 3–16 tecken, bara bokstäver, siffror och _.';
    if (RESERVED_RE.test(name)) return 'Det namnet är reserverat.';
    return null;
  }

  register(name, password) {
    const key = name.toLowerCase();
    if (this.users.has(key)) return 'Användarnamnet är upptaget.';
    const salt = crypto.randomBytes(16).toString('hex');
    this.users.set(key, { name, salt, hash: hash(password, salt), created: new Date().toISOString() });
    this.save();
    return null;
  }

  /** Returnerar kontot om lösenordet stämmer, annars null. */
  check(name, password) {
    const user = this.users.get(name.toLowerCase());
    if (!user) return null;
    const a = Buffer.from(hash(password, user.salt), 'hex');
    const b = Buffer.from(user.hash, 'hex');
    return crypto.timingSafeEqual(a, b) ? user : null;
  }

  /** Stänger av eller återställer ett konto. Returnerar false om kontot inte finns. */
  setBanned(name, banned) {
    const user = this.users.get(String(name).toLowerCase());
    if (!user) return false;
    if (banned) user.banned = true;
    else delete user.banned;
    this.save();
    return true;
  }

  list() {
    return [...this.users.values()].map((u) => ({ name: u.name, created: u.created, banned: !!u.banned }));
  }

  save() {
    const tmp = this.file + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(Object.fromEntries(this.users), null, 2));
    fs.renameSync(tmp, this.file);
  }
}

function hash(password, salt) {
  return crypto.scryptSync(password, salt, 32).toString('hex');
}

module.exports = { Accounts };
