'use strict';

// Texter och listor som servern använder: intressen och isbrytarfrågor.

/** Intressen man kan välja i appen. Id:t skickas över nätet, appen visar sin egen etikett. */
const INTERESTS = ['spel', 'musik', 'sport', 'film', 'bocker', 'mat', 'djur', 'resor', 'teknik', 'konst'];

const ICEBREAKERS = [
  'Om du fick välja en superkraft, vilken skulle det bli?',
  'Vilken är den bästa filmen eller serien du sett i år?',
  'Vad skulle du göra med en helt ledig dag?',
  'Vilket land skulle du helst vilja resa till?',
  'Vad är din favoritmat?',
  'Katt eller hund?',
  'Vilken låt har du lyssnat mest på senaste tiden?',
  'Om du kunde lära dig något direkt, vad skulle det vara?',
  'Vad är det roligaste du har varit med om?',
  'Sommar eller vinter?',
  'Vilket spel skulle du rekommendera till en kompis?',
  'Vad vill du jobba med i framtiden?',
  'Pizza eller hamburgare?',
  'Om du fick träffa vem som helst, levande eller död, vem skulle det vara?',
  'Vad är något du är riktigt bra på?',
  'Morgonmänniska eller nattuggla?',
];

/**
 * Reaktioner man kan sätta på ett meddelande. Bara tecken som finns i vanliga typsnitt,
 * så att de syns likadant i Unity-appen (som inte kan rita färg-emoji) och på webben.
 */
const REACTIONS = ['♥', '★', '☺'];

module.exports = { INTERESTS, ICEBREAKERS, REACTIONS };
