# syncUp

Skolprojekt. En chatt där man pratar med någon slumpad, anonymt eller med konto. Finns som app i Unity och som hemsida, och båda kan chatta med varandra.

## Funktioner

- Chatta anonymt eller logga in
- Välj intressen och matcha med någon som gillar samma sak
- Fråga-knapp som ger en slumpad fråga att prata om
- Reagera på meddelanden med ♥ ★ ☺
- Se när den andra skriver
- syncBot, en bot att testa med om ingen annan är online
- Telefonnummer, mejl och fula ord döljs
- Rapportera och adminpanel (kicka, stänga av konton, QR-kod för att bjuda in)

## Starta

Man behöver [Node.js](https://nodejs.org).

- Dubbelklicka på `Starta syncUp.bat`. Chatten och adminpanelen öppnas.
- Eller tryck Play i Unity, då startar servern av sig själv.

Andra i samma nätverk kan skanna QR-koden i adminpanelen.

## Online

Servern ligger på Render: https://syncup-ptcn.onrender.com

Välj "Online" i appen. Servern somnar om ingen använt den på en stund, så öppna sidan lite innan.

## Tester

```
cd server
npm test
```
