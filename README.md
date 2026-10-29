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
| Váhy v int8 (`iris quantize`), jadrá AVX2/FMA, vlákna | F2 (PR #2) |
| Delenie textu na vety, preklad odsekov s rozložením | F2 (PR #2) |
| chrF (ako sacreBLEU), meranie na FLORES-200 v CI | F2 (PR #2) |
| Pamäť prekladov a slovník mien | F2 – ďalší krok |

## Štruktúra
- `src/Iris.Core` – jadro: `Tokenization`, `Model` (formát `.iris`, int8), `Numerics` (výpočty, jadrá AVX2),
  `Translation` (Marian, hľadanie prekladu, tokenizér Marian, delenie na vety, overenie proti referencii),
  `Evaluation` (chrF).
- `src/Iris.Cli` – príkaz `iris` (`tokenize`, `info`, `translate`, `verify`).
- `tests/Iris.Tests` – vlastný spúšťač testov (`dotnet run --project tests/Iris.Tests`).
- `tools/reference` – skripty v Pythone, ktoré vyrábajú referenčné dáta (len pre CI a testy).

## Príkazy
```
iris translate <model.iris> [--beams N | --greedy] [--int8] [--ids] [text]   preklad textu alebo každého riadku vstupu
iris quantize <model.iris> <model-int8.iris>        int8 kópia modelu (asi štvrtina veľkosti)
iris bench <model.iris> [--max-seconds 1]            čas prekladu nadpisu a úvodu článku (brána F2)
iris score <model.iris> <src.txt> <ref.txt>          preklad testovacej sady a chrF
iris chrf <preklady.txt> <referencie.txt>            chrF dvoch súborov
iris verify <model.iris> <reference.json> [--min-greedy 0.99] [--min-beam 0.95]
iris info <model.iris>
iris tokenize <model.spm> <text>
```

## Overenie jadra (CI)
Úloha **Reference (transformers)** v CI: malý náhodný model Marian sa musí zhodovať na 100 %, potom sa stiahne
`Helsinki-NLP/opus-mt-en-sk` (len s licenciou Apache-2.0), prevedie do `.iris` a pre 500 vymyslených viet
(`tools/reference/sentences.py`) sa porovnajú ID tokenov s transformers. Brána F1: **greedy ≥ 99 %, lúč ≥ 95 %**,
tokenizér 100 %. Výsledok je v anotáciách behu.

Úloha **Quality and speed (F2)**: int8 kópia skutočného modelu, čas prekladu nadpisu a úvodu článku (brána
**≤ 1 s**, greedy aj lúč) a chrF na **FLORES-200 devtest** (len meranie, nikdy učenie; CC BY-SA 4.0) – int8 nesmie
byť o viac než 0,5 chrF horší než float32.

## Formát `.iris`
`IRISMDL1` · dĺžka hlavičky (8 B, little endian) · hlavička JSON (`kind`, `config`, `provenance`, tabuľka tenzorov a
blobov) · dáta, každá položka zarovnaná na 64 B. Model nesie aj svoj pôvod a licenciu.

## Licencia
Apache-2.0. Žiadna telemetria.
