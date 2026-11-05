# syncUp

Ett skolprojekt där man chattar med någon ny, anonymt eller med konto. Det finns en app gjord i Unity och en
webbversion, och båda pratar med samma server, så Unity- och webbanvändare kan chatta med varandra.

## Funktioner

- **Anonym chatt eller konto.** Anonyma får ett namn som `Anonym#4821`. Konton matchas bara med andra konton.
- **Intressen.** Välj till exempel musik och spel, så matchas du helst med någon som gillar samma sak.
- **Isbrytare.** Knappen *Fråga* ger båda en slumpad fråga att prata om.
- **Reaktioner.** Klicka på ett meddelande och reagera med ♥ ★ ☺.
- **"Skriver..."** visas när den andra skriver.
- **syncBot.** En testbot att prata med när ingen annan är online.
- **Säkerhet.** Telefonnummer, mejladresser och länkar döljs, grova ord maskeras, och det finns spamskydd.
- **Rapportera.** Välj orsak; de senaste meddelandena sparas till admin.
- **Adminpanel.** Statistik i realtid, QR-kod för att bjuda in, meddelande till alla, kicka och stänga av konton.

## Kom igång (lokalt)

Kräver [Node.js](https://nodejs.org) (version 22 eller nyare för testerna).

1. Dubbelklicka på **`Starta syncUp.bat`**. Servern startar och chatten och adminpanelen öppnas i webbläsaren.
2. Klasskompisar i samma nätverk skannar QR-koden i adminpanelen, eller skriver adressen som står i servern-fönstret.
3. I Unity: tryck på **Play**. Servern startar automatiskt om den inte redan körs
   (stängs av och på i menyn *syncUp > Starta servern vid Play*).

Välj **Den här datorn** på startskärmen i appen när servern körs lokalt.

## Köra online (Render)

Då fungerar syncUp från vilket nätverk som helst, även när din dator är avstängd.

1. Skapa ett gratis konto på [render.com](https://render.com) och koppla ditt GitHub-konto.
2. Välj **New > Blueprint** och välj det här repot. Render läser `render.yaml` och sätter upp allt.
3. När det är klart får du en adress, till exempel `https://syncup-abcd.onrender.com`.
   - Chatten: öppna adressen i webbläsaren.
   - Unity: välj **Online** på startskärmen och skriv `wss://syncup-abcd.onrender.com`
     (eller ändra `onlineServer` på ChatApp så att det blir förvalt).
   - Adminpanelen: `https://syncup-abcd.onrender.com/admin?key=NYCKEL`. Nyckeln står under *Environment* hos Render.

Att tänka på med gratisnivån: servern somnar efter 15 minuter utan besökare och tar ungefär en minut att vakna,
och konton och rapporter försvinner när den startas om. Öppna sidan en stund innan presentationen.

## Mappar

| Mapp | Innehåll |
|---|---|
| `Assets/Scripts` | Unity-appen: `ChatApp.cs` (skärmar och logik), `Ui.cs` (utseende), `NetClient.cs` (nätverk) |
| `Assets/Scripts/Editor` | Startar servern vid Play och ställer in bygget (fönster och ikon) |
| `server` | Servern i Node.js, utan externa paket. Protokollet beskrivs i `server/hub.js` |
| `server/public` | Webbversionen av chatten |

## Tester

```
cd server
npm test
```

Testerna startar en egen server och provar alla funktioner automatiskt.
