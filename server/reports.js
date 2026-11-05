'use strict';

// Rapporter: sparas som läsbara textblock i reports.log.

const fs = require('fs');

const SEPARATOR = '=== ';

class Reports {
  constructor(file) {
    this.file = file;
  }

  /** Sparar en rapport. `history` är samtalets senaste meddelanden: [{ t, from, text }]. */
  write({ reporter, reporterMode, reported, reason, history }, done) {
    const lines = history.map((h) => `[${new Date(h.t).toISOString().slice(11, 19)}] ${h.from}: ${h.text}`);
    const block = [
      `${SEPARATOR}${new Date().toISOString()} ===`,
      `Rapporterad av: ${reporter} (${reporterMode})`,
      `Rapporterad användare: ${reported}`,
      `Orsak: ${reason || '(ingen angiven)'}`,
      'Senaste meddelanden:',
      ...(lines.length ? lines : ['(inga meddelanden)']),
      '',
      '',
    ].join('\n');
    fs.appendFile(this.file, block, done);
  }

  /** Alla rapporter som textblock, nyast först. */
  read() {
    let text;
    try {
      text = fs.readFileSync(this.file, 'utf8');
    } catch {
      return [];
    }
    return text
      .split(new RegExp('^(?=' + SEPARATOR + ')', 'm'))
      .map((block) => block.trim())
      .filter(Boolean)
      .reverse();
  }

  clear() {
    fs.writeFileSync(this.file, '');
  }
}

module.exports = { Reports };
