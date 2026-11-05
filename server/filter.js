'use strict';

// Döljer kontaktuppgifter och grova ord (från badwords.txt).

const fs = require('fs');

function loadBadWords(file) {
  try {
    return fs
      .readFileSync(file, 'utf8')
      .split(/\r?\n/)
      .map((line) => line.trim().toLowerCase())
      .filter((line) => line && !line.startsWith('#'));
  } catch {
    return [];
  }
}

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function createFilter(badWordsFile) {
  const badWords = loadBadWords(badWordsFile);
  const badRe = badWords.length
    ? new RegExp('(?<![\\p{L}\\p{N}])(' + badWords.map(escapeRe).join('|') + ')(?![\\p{L}\\p{N}])', 'giu')
    : null;

  function filterText(text) {
    let t = text.replace(/[\u0000-\u001f\u007f]/g, ' ');
    // måste vara en sträng, U+2028 räknas som radbrytning i ett vanligt regex
    t = t.replace(new RegExp('[\u200b-\u200f\u2028\u2029\ufeff]', 'g'), ''); // osynliga tecken
    t = t.replace(/[\w.+-]+@[\w-]+\.[\w.]+/g, '[dolt]');                   // e-post
    t = t.replace(/(https?:\/\/|www\.)\S+/gi, '[dolt]');                    // länkar
    t = t.replace(/\b[\w-]+\.(com|se|net|org|io|gg|ly|me)\b/gi, '[dolt]');  // domäner
    t = t.replace(/@\w{2,}/g, '[dolt]');                                    // @användarnamn
    t = t.replace(/\+?\d(?:[\s\-.]?\d){6,}/g, '[dolt]');                    // telefonnummer
    if (badRe) t = t.replace(badRe, (m) => '*'.repeat(m.length));
    return t.replace(/\s+/g, ' ').trim();
  }

  return { filterText, wordCount: badWords.length };
}

module.exports = { createFilter };
