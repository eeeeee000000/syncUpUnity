'use strict';

// Begränsningar per IP-adress och säkerhetsrubriker.

// new RateLimiter(5, 60 * 60 * 1000) = max 5 gånger per timme
class RateLimiter {
  constructor(max, windowMs) {
    this.max = max;
    this.windowMs = windowMs;
    this.hits = new Map(); // nyckel -> { count, resetAt }
    this.timer = setInterval(() => this.cleanup(), windowMs);
    this.timer.unref();
  }

  hit(key) {
    const now = Date.now();
    let entry = this.hits.get(key);
    if (!entry || now >= entry.resetAt) {
      entry = { count: 0, resetAt: now + this.windowMs };
      this.hits.set(key, entry);
    }
    if (entry.count >= this.max) return false;
    entry.count++;
    return true;
  }

  blocked(key) {
    const entry = this.hits.get(key);
    return !!entry && Date.now() < entry.resetAt && entry.count >= this.max;
  }

  cleanup() {
    const now = Date.now();
    for (const [key, entry] of this.hits) if (now >= entry.resetAt) this.hits.delete(key);
  }
}

// På Render kommer den riktiga adressen i X-Forwarded-For. Den går att fuska med,
// men det räcker för att bromsa spam.
function clientIp(req, behindProxy) {
  if (behindProxy) {
    const forwarded = String(req.headers['x-forwarded-for'] || '').split(',')[0].trim();
    if (forwarded) return forwarded;
  }
  return req.socket.remoteAddress || 'okänd';
}

class ConnectionCounter {
  constructor(max) {
    this.max = max;
    this.open = new Map();
  }

  add(ip) {
    const count = this.open.get(ip) || 0;
    if (count >= this.max) return false;
    this.open.set(ip, count + 1);
    return true;
  }

  remove(ip) {
    const count = (this.open.get(ip) || 1) - 1;
    if (count <= 0) this.open.delete(ip);
    else this.open.set(ip, count);
  }
}

function securityHeaders(isAdminPage) {
  const csp = [
    "default-src 'self'",
    // adminsidan har sin css och js i samma fil
    isAdminPage ? "script-src 'self' 'unsafe-inline'" : "script-src 'self'",
    isAdminPage ? "style-src 'self' 'unsafe-inline'" : "style-src 'self'",
    "img-src 'self' data:",
    "connect-src 'self' ws: wss:",
    "frame-ancestors 'none'",
    "base-uri 'none'",
    "form-action 'self'",
  ].join('; ');
  return {
    'Content-Security-Policy': csp,
    'X-Content-Type-Options': 'nosniff',
    'X-Frame-Options': 'DENY',
    'Referrer-Policy': 'no-referrer',
  };
}

module.exports = { RateLimiter, ConnectionCounter, clientIp, securityHeaders };
