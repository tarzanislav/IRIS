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
| Transformer Marian (enkóder, dekóder, KV cache), greedy + lúč | ďalší krok |
| Prevod OPUS-MT → `.iris` a referencia v CI | ďalší krok |

## Štruktúra
- `src/Iris.Core` – jadro: `Tokenization`, `Model` (formát `.iris`), `Numerics` (výpočty).
- `src/Iris.Cli` – príkaz `iris` (`tokenize`, `info`).
- `tests/Iris.Tests` – vlastný spúšťač testov (`dotnet run --project tests/Iris.Tests`).
- `tools/reference` – skripty v Pythone, ktoré vyrábajú referenčné dáta (len pre CI a testy).

## Formát `.iris`
`IRISMDL1` · dĺžka hlavičky (8 B, little endian) · hlavička JSON (`kind`, `config`, `provenance`, tabuľka tenzorov a
blobov) · dáta, každá položka zarovnaná na 64 B. Model nesie aj svoj pôvod a licenciu.

## Licencia
Apache-2.0. Žiadna telemetria.
