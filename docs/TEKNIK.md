# Tekniken bakom syncUp

Unity-appen och hemsidan pratar med samma server över WebSocket. Därför kan någon i appen chatta med någon på hemsidan.

## Servern

Servern är skriven i Node.js och använder inga paket utöver det som följer med Node. Den kör både en vanlig webbserver (hemsidan och adminpanelen) och en WebSocket-server på samma port.

Vi valde WebSocket i stället för vanliga HTTP-anrop eftersom anslutningen hålls öppen. Servern kan skicka ett nytt meddelande direkt, utan att klienten hela tiden måste fråga om det finns något nytt.

| Fil | Vad den gör |
|---|---|
| `server.js` | Startar servern och läser inställningar |
| `hub.js` | Håller koll på alla anslutna, köer, matchning och chattrum |
| `miniws.js` | Vår egen WebSocket-server, så att vi inte behöver paketet "ws" |
| `accounts.js` | Konton, sparas i `users.json` med lösenord hashade med scrypt |
| `filter.js` | Döljer telefonnummer, mejladresser och fula ord |
| `bot.js` | syncBot, letar efter vissa ord och svarar med färdiga meningar |
| `content.js` | Intressen, frågor och reaktioner |
| `security.js` | Begränsningar per IP-adress och säkerhetsrubriker |
| `admin.js`, `admin.html` | Adminpanelen |
| `test.js` | Automatiska tester, körs med `npm test` |

### Meddelanden

Allt som skickas är JSON med ett `type`-fält, till exempel:

```json
{ "type": "chat", "text": "hej!" }
```

Klienten skickar bland annat `anon`, `login`, `next`, `chat`, `typing` och `report`. Servern svarar med till exempel `matched`, `chat`, `typing` och `partner_left`. Appen och hemsidan skickar exakt samma meddelanden.

### Matchning

1. Den som trycker "Chatta anonymt", loggar in eller trycker "Nästa" hamnar i en kö. Anonyma och inloggade har varsin kö.
2. En gång i sekunden försöker servern para ihop folk i kön.
3. Den som väntat längst får den person som har flest gemensamma intressen.
4. Om båda valt intressen måste de ha något gemensamt, men efter 5 sekunder släpps kravet så att ingen fastnar i kön.
5. Efter "Nästa" matchas man inte med samma person igen på 8 sekunder.

### Skydd mot spam

- Max 300 tecken per meddelande och max 12 meddelanden per 5 sekunder.
- Max 5 felaktiga inloggningar per anslutning och 10 per IP-adress och kvart.
- Max 5 nya konton per IP-adress och timme.
- Samma konto kan inte vara inloggat på två ställen samtidigt.
- Servern pingar alla klienter och stänger anslutningar som slutat svara.

Chattar sparas inte. De senaste 30 meddelandena i ett rum finns bara i minnet så att en rapport kan visa vad som hände.

## Unity-appen

Koden finns i `Assets/Scripts/`.

| Fil | Vad den gör |
|---|---|
| `ChatApp.cs` | Hela appen: startskärm, inloggning och chatt |
| `Ui.cs` | Färger och hjälpfunktioner för knappar och textfält |
| `NetClient.cs` | WebSocket-anslutningen till servern |

Gränssnittet byggs i kod när appen startar, så scenen är tom. Det gör det lättare att hålla appen och hemsidan lika.

WebSocket-meddelanden tas emot på en bakgrundstråd, men Unity låter bara huvudtråden ändra gränssnittet. Därför läggs inkommande meddelanden i en `ConcurrentQueue` som töms i `Update()` varje bildruta.

På startskärmen väljer man server: lokal (`ws://localhost:8080`) eller online (`wss://syncup-ptcn.onrender.com`).

I editorn startar `Editor/LocalServerLauncher.cs` servern automatiskt när man trycker Play och stänger den när man slutar.

## Hemsidan

Finns i `server/public/`: `index.html`, `style.css` och `app.js`. Den gör samma sak som appen och ansluter till WebSocket på samma adress som sidan laddades från.

## Adminpanelen

Där ser man vilka som är online och vilka chattar som pågår. Man kan läsa rapporter, kicka folk, stänga av konton, skicka meddelanden till alla och visa en QR-kod så att andra kan gå med. Den skyddas med en nyckel och går lokalt bara att öppna på datorn där servern körs.

## Online

Servern körs gratis på Render och byggs om automatiskt när vi pushar till GitHub (`render.yaml`). Gratisservern somnar när ingen använt den på en stund och tar ungefär en minut att vakna.
