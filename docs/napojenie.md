# Napojenie IRIS do inej aplikácie

Ako použiť prekladač IRIS v programe v C# (.NET 10), napríklad v UNIVERSUM. IRIS sa pripája ako knižnica
`Iris.Core.dll` a súbor modelu; beží v procese aplikácie, bez siete a bez ďalších balíkov.

## 1. Čo stiahnuť

Z vydania IRIS (https://github.com/tarzanislav/IRIS/releases) dve prílohy a súbor súčtov:

| Príloha | Na čo |
| --- | --- |
| `Iris.Core.dll` | knižnica (pridať ako referenciu) |
| `opus-mt-en-sk-int8.iris` | model EN→SK, 79 MB |
| `SHA256SUMS.txt` | súčty SHA-256 – každú prílohu pred použitím overiť |
| `LICENSE`, `NOTICE.txt` | licencie a pôvod – priložiť k aplikácii (napr. do okna „O programe“) |

Repozitár IRIS je verejný, sťahovanie nepotrebuje token. V CI si verziu a súčty pripnite (pevný tag a SHA-256),
aby sa preklad nezmenil bez vášho vedomia.

## 2. Preklad

```csharp
using Iris;
using Iris.Memory;
using Iris.Translation;

// raz za beh aplikácie (asi 1 s): mimo vlákna rozhrania
var translator = await Task.Run(() => Translator.Load(modelPath));

// spoločná pamäť opráv a slovník mien (pozri 3 a 4)
var memory = TranslationMemory.Open(IrisPaths.Memory("en-sk"));
var glossary = new Glossary();
glossary.Add("Harbor Lights");                    // názov ostane, ako je
glossary.Add("The Glass Orchard", "Sklenený sad"); // názov s oficiálnym slovenským tvarom

// preklad textu: vety, odseky a riadky ostanú na mieste
var progress = new Progress<TranslationProgress>(p => statusText = $"{p.Done}/{p.Total}");
var result = await Task.Run(() => translator.TranslateText(text, memory: memory, glossary: glossary,
    cancellation: token, progress: progress));

showText(result.Text);
foreach (var sentence in result.Sentences)
{
    // sentence.Source, sentence.Target, sentence.Origin (Model / Memory / Glossary), sentence.Note
}
```

- **Lúč alebo greedy:** bez `beams` sa použije lúč modelu (4 lúče, najlepšia kvalita); `beams: 1` je greedy, asi 2×
  rýchlejší. Na článok odporúčame lúč, na náhľad počas písania greedy.
- **Rýchlosť:** nadpis a úvod článku 0,1–0,4 s na bežnom PC (int8, všetky jadrá).
- **Vlákna:** jeden `Translator` pre celú aplikáciu; smie prekladať z viacerých vlákien naraz. Volajte ho mimo vlákna
  rozhrania (`Task.Run`); `Progress<T>` vráti priebeh do vlákna rozhrania sám.
- **Zrušenie:** `cancellation` sa kontroluje pred každou vetou (`OperationCanceledException`).
- **Dlhé vety** IRIS preloží po častiach; `Note` to oznámi. Výnimku pri preklade textu nevyhadzuje.

## 3. Opravy prekladu (pamäť prekladov)

Opravuje sa **po vetách**: rozhranie má ukázať vetu (`sentence.Source`) a jej preklad a dovoliť ho prepísať.

```csharp
memory.Remember(sentence.Source, correctedTranslation); // nabudúce sa veta preloží takto
memory.Forget(sentence.Source);                         // späť na preklad modelu
```

- `IrisPaths.Memory("en-sk")` je **spoločný súbor všetkých programov IRIS na tomto PC** (UNIVERSUM, aplikácia IRIS):
  oprava urobená v jednom je známa v druhom. Zápis chráni zámok, takže viac programov naraz nič nepokazí.
- Umiestnenie: Windows `%LOCALAPPDATA%\IRIS`, Linux `~/.local/share/IRIS`, macOS `~/Library/Application Support/IRIS`;
  premenná prostredia `IRIS_HOME` ho presunie (napr. pre testy).
- Pamäť je to, z čoho sa IRIS neskôr učí (F3) – každá oprava sa počíta.

## 4. Slovník mien a názvov

Mená ľudí a názvy filmov či seriálov model nesmie prekladať. Pred prekladom ich pridajte do slovníka:

- `glossary.Add(meno)` – ostane, ako je napísané (osoby, väčšina názvov),
- `glossary.Add(originál, slovenskýNázov)` – ak má dielo oficiálny slovenský názov,
- slovník sa dá uložiť a načítať (`Save`, `Glossary.Load`, TSV „originál TAB preklad“);
  `IrisPaths.Glossary("en-sk")` je spoločné miesto.

Hľadajú sa celé slová s presnými písmenami, najdlhší termín vyhráva; aj 20 000 termínov je rýchlych.
Ak názov prekladom „neprežije“, IRIS vetu preloží bez slovníka a povie to v `Note`.

## 5. Čo ukázať používateľovi

- Pôvod vety (`Origin`): model, pamäť (tvoja oprava) alebo slovník – napr. jemné zvýraznenie.
- `Note`, ak nie je prázdna (napr. dlhá veta preložená po častiach).
- Licencie a pôvod modelu z `NOTICE.txt` v okne „O programe“.
