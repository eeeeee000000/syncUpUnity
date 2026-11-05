'use strict';

// syncBot: en enkel regelbaserad chattbot så att man kan testa appen även när ingen annan är online.

const BOT_NAME = 'syncBot';

const GREETING = 'Hej! Jag är en testbot. Skriv något så svarar jag.';

/**
 * Matchar hela ord, även ord med å, ä och ö (vanliga \b räknar inte dem som bokstäver).
 * Ett ord som slutar på * får fortsätta, t.ex. 'spel*' matchar "spelar" och "spelet".
 */
function words(...list) {
  const parts = list.map((w) => (w.endsWith('*') ? w.slice(0, -1) + '\\p{L}*' : w));
  return new RegExp('(?<!\\p{L})(' + parts.join('|') + ')(?!\\p{L})', 'u');
}

// Första regeln som matchar vinner, så de mest specifika ligger först.
const RULES = [
  [words('hejdå', 'hej då', 'vi ses', 'bye', 'ses'), 'Hejdå! Tryck på Nästa för att hitta någon ny att prata med.'],
  [words('vad heter du', 'vem är du', 'ditt namn'), 'Jag heter syncBot. Jag finns här så att du kan testa syncUp även när ingen annan är online.'],
  [words('bot', 'robot', 'människa'), 'Japp, jag är en bot. Men jag gör mitt bästa för att vara trevlig!'],
  [words('hur mår du', 'läget', 'hur är det'), 'Jag mår bra, tack! Hur mår du själv?'],
  [words('hur gammal', 'din ålder'), 'Jag skapades i ett skolprojekt, så jag är ganska ung!'],
  [words('hej', 'hejsan', 'tjena', 'tjenare', 'tja', 'hallå', 'halloj', 'god morgon'), 'Hej hej! Vad kul att du hittade hit. Vad gör du just nu?'],
  [words('trött', 'ledsen', 'tråkigt', 'dåligt', 'dålig', 'stressad'), 'Åh, det låter jobbigt. Hoppas dagen blir bättre! Vill du prata om något kul istället?'],
  [words('bra', 'fint', 'toppen', 'okej', 'ok', 'najs', 'grymt'), 'Vad skönt att höra! Vad gillar du att göra på fritiden?'],
  [words('spel*', 'gaming', 'gamer', 'minecraft', 'fortnite', 'roblox'), 'Spel är kul! Vilket spel spelar du mest just nu?'],
  [words('musik', 'låt*', 'lyssna*', 'artist*'), 'Musik är bäst. Vilken artist lyssnar du mest på?'],
  [words('sport*', 'fotboll', 'hockey', 'träna*', 'gym*', 'basket'), 'Snyggt! Tränar du själv, eller tittar du mest?'],
  [words('skola*', 'skolan', 'lektion*', 'läxa*', 'läxor', 'prov', 'proven', 'lärare*'), 'Skolan, ja. Vilket är ditt favoritämne?'],
  [words('mat', 'maten', 'äta', 'pizza', 'hamburgare', 'hungrig'), 'Nu blev jag sugen, fast jag inte kan äta. Vad är din favoritmat?'],
  [words('film*', 'serie*', 'netflix'), 'Har du sett något bra nyligen? Jag behöver tips!'],
  [words('djur*', 'hund*', 'katt*'), 'Djur är underbara. Har du några husdjur?'],
  [words('tack', 'tackar'), 'Varsågod!'],
];

const FALLBACKS = [
  'Intressant! Berätta mer.',
  'Okej! Vad tycker du om det?',
  'Spännande. Vad har du för planer i helgen?',
  'Haha, förstår. Vad brukar du göra efter skolan?',
  'Kul! Tryck på Fråga om du vill ha något att prata om.',
];

/** Väljer ett svar på det användaren skrev. `last` är förra svaret, så att boten inte upprepar sig. */
function botReply(text, last) {
  const t = String(text).toLowerCase();
  for (const [re, answer] of RULES) {
    if (re.test(t) && answer !== last) return answer;
  }
  if (t.trim().endsWith('?')) {
    const answer = 'Bra fråga! Vad tror du själv?';
    if (answer !== last) return answer;
  }
  const options = FALLBACKS.filter((f) => f !== last);
  return options[Math.floor(Math.random() * options.length)];
}

function botIcebreakerReply() {
  return 'Oj, svår fråga! Jag får fundera lite. Vad svarar du själv?';
}

/** Hur länge boten "skriver" innan svaret kommer, så att det känns mer naturligt. */
function typingDelay(answer, speed = 1) {
  return Math.min(2500, 600 + answer.length * 20) * speed;
}

module.exports = { BOT_NAME, GREETING, botReply, botIcebreakerReply, typingDelay };
