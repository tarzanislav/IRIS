# IRIS

**Vlastný strojový prekladač** – čisté jadro v C# (.NET 10), bez cudzích knižníc za behu, s otvorenou licenciou
(Apache-2.0) na súkromné aj komerčné použitie. Smer **EN→SK**, potom **CS→SK**.

## Princíp
1. **Najprv vlastné jadro, cudzie váhy:** jadro (tokenizér, transformer, vyhľadávanie lúčom) je náš kód; na začiatok
   načíta otvorené váhy OPUS-MT (Apache-2.0) prevedené do vlastného formátu `.iris`.
2. **Overenie proti referencii:** Python beží **len v CI** – vyrobí referenčné výstupy a jadro ich musí zopakovať.
3. **Odpútanie:** vlastné učenie (dáta s overenou licenciou, učiteľ s otvorenou licenciou, opravy používateľa) a
   nakoniec vlastný model.

Preklady komerčných služieb sa na učenie ani na hodnotenie **nepoužívajú** (ich licencie to zakazujú).

## Stav
| Časť | Stav |
|---|---|
| Tokenizér SentencePiece (unigram, normalizácia, byte fallback) | hotový, zhodný so SentencePiece |
| Formát modelu `.iris` (čítanie aj zápis) | hotový |
| Výpočtové jadrá (SIMD, paralelne) | hotové |
| Transformer Marian (enkóder, dekóder, KV cache) | hotový (F1) |
| Hľadanie prekladu: greedy a lúč (rovnaké pravidlá ako transformers 5) | hotové (F1) |
| Tokenizér Marian (spoločný slovník, špeciálne tokeny, jazykový kód) | hotový (F1) |
| Prevod OPUS-MT → `.iris` (kontrola licencie) a referencia v CI | hotové (F1) |
| Váhy v int8 (`iris quantize`), jadrá AVX2/FMA, vlákna | hotové (F2) |
| Delenie textu na vety, preklad odsekov s rozložením, delenie príliš dlhých viet | hotové (F2) |
| chrF (ako sacreBLEU), meranie na FLORES-200 v CI | hotové (F2) |
| Pamäť prekladov a slovník mien | hotové (F2) |
| Vydanie (knižnica, `iris.exe`, int8 model, SHA-256) | v0.1.0, v0.2.0 (napojenie), v0.3.0 (aplikácia, balíky) |
| Ďalšie smery EN→CS, SK→EN, CS→EN (overené ako jazykové balíky) | hotové (CI) |
| Aplikácia IRIS pre Windows (preklad, súbory, pamäť, rýchly preklad, SK/CS/EN) | A1 – hotová |
| Učenie z opráv | F3 – ďalší krok |

## Štruktúra
- `src/Iris.Core` – jadro: `Tokenization`, `Model` (formát `.iris`, int8), `Numerics` (výpočty, jadrá AVX2),
  `Translation` (Marian, hľadanie prekladu, tokenizér Marian, delenie na vety, `Translator`), `Memory` (pamäť
  prekladov, slovník mien), `Evaluation` (chrF). Verejné API: `Translator`, `TranslationMemory`, `Glossary`,
  `ModelFile`, `Chrf`; výpočtové jadrá sú interné.
- `src/Iris.Cli` – príkaz `iris` a overenie proti referencii (`verify`).
- `src/Iris.Assistant` – logika aplikácie (jazykové balíky, preklad cez angličtinu, súbory, história, nastavenia,
  texty rozhrania); beží na každej platforme.
- `src/Iris.App` – aplikácia pre Windows (WPF), `iris-app.exe`; `--selftest` postaví každé okno.
- `tests/Iris.Tests` – vlastný spúšťač testov (`dotnet run --project tests/Iris.Tests`).
- `tools/reference` – skripty v Pythone, ktoré vyrábajú referenčné dáta (len pre CI a testy).
- `tools/hardware/iris-hw.bat` – prehľad hardvéru počítača pre ladenie rýchlosti (žiadne osobné údaje).

## Príkazy
```
iris translate <model.iris> [--beams N | --greedy] [--int8] [--memory pamäť.jsonl] [--glossary slovník.tsv] [text]
                                                     preklad textu alebo každého riadku vstupu (--ids: holý model)
iris remember <pamäť.jsonl> <veta> <preklad>         zapamätá opravený preklad vety
iris quantize <model.iris> <model-int8.iris>        int8 kópia modelu (asi štvrtina veľkosti)
iris bench <model.iris> [--runs 3] [--max-seconds 1] čas prekladu nadpisu a úvodu článku (brána F2)
iris score <model.iris> <src.txt> <ref.txt>          preklad testovacej sady a chrF
iris chrf <preklady.txt> <referencie.txt>            chrF dvoch súborov
iris verify <model.iris> <reference.json> [--min-greedy 0.99] [--min-beam 0.95]
iris info <model.iris>
iris tokenize <model.spm> <text>
```
Neznáma voľba je chyba (kód 2), chyba vstupu kód 1.

## Napojenie do inej aplikácie
Návod s príkladom v C# (preklad, zrušenie, priebeh, spoločná pamäť opráv, slovník mien): [`docs/napojenie.md`](docs/napojenie.md).

## Overenie jadra (CI)
Úloha **Reference (transformers)** v CI: malý náhodný model Marian sa musí zhodovať na 100 %, potom sa stiahne
`Helsinki-NLP/opus-mt-en-sk` (len s licenciou Apache-2.0), prevedie do `.iris` a pre 500 vymyslených viet
(`tools/reference/sentences.py`) sa porovnajú ID tokenov s transformers. Brána F1: **greedy ≥ 99 %, lúč ≥ 95 %**,
tokenizér 100 %. Výsledok je v anotáciách behu.

Úloha **Quality and speed (F2)**: int8 kópia skutočného modelu, čas prekladu nadpisu a úvodu článku a chrF na
**FLORES-200 devtest** (len meranie, nikdy učenie; CC BY-SA 4.0) – int8 nesmie byť o viac než 0,5 chrF horší než
float32. **Brána rýchlosti je relatívna:** int8 (greedy aj lúč) nesmie byť o viac než 10 % pomalší než základný commit
na tom istom runneri (zdieľané runnery majú veľmi rozdielnu rýchlosť pamäte). Cieľ **≤ 1 s** sa v CI len hlási
a overuje sa na počítačoch vlastníka (`iris bench`).

Úloha **Windows**: zostavenie a testy na Windows, `iris.exe` ako vo vydaní a skript hardvéru (PowerShell 5.1 aj 7).
Testy bežia aj bez AVX2 a bez vektorových inštrukcií (cesty starších procesorov).

## Vydanie
Spúšťa sa ručne: **Actions → Release → Run workflow** (verzia, napr. `0.1.0`), beží až po schválení vlastníkom
v prostredí `release`. Vydá sa len commit vetvy `main`, ktorý prešiel všetkými bránami CI. Súčasti vydania:
`Iris.Core.dll`, `iris-VERZIA-win-x64.zip` (samostatný `iris.exe`), `opus-mt-en-sk-int8.iris`, `LICENSE`,
`NOTICE.txt` (pôvod a licencie) a `SHA256SUMS.txt`. Poznámky k vydaniu sú v `docs/releases/`.

## Hardvér
`tools\hardware\iris-hw.bat` – spustí sa dvojklikom, opýta sa na názov počítača a zapíše `iris-hw-<názov>.txt`
(procesor a jeho inštrukcie, pamäť a rýchlosť jej čítania, grafika, disky, systém, .NET); výsledok skopíruje do
schránky. Nezbiera mená, adresy ani súbory. Je to jeden súbor: dávka, ktorá sa spustí v PowerShelli.

## Formát `.iris`
`IRISMDL1` · dĺžka hlavičky (8 B, little endian) · hlavička JSON (`kind`, `config`, `provenance`, tabuľka tenzorov a
blobov) · dáta, každá položka zarovnaná na 64 B. Model nesie aj svoj pôvod a licenciu.

## Licencia
Apache-2.0. Žiadna telemetria.
