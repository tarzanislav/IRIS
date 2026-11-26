# Trezor IRIS – súkromné licencované dáta

Niektoré dáta smie IRIS používať len súkromne, napr. paralelný korpus **InterCorp** (ČNK): len na výskum, nič
komerčné, **žiadne zverejnenie dát ani toho, čo sa z nich naučí** (zverejniť sa smie len miera kvality, napr. chrF).
Trezor IRIS drží takéto dáta a všetko, čo sa z nich naučí, **zašifrované**, a „stena“ zabráni, aby sa niečo z nich
dostalo do verejného učenia, repozitára alebo vydania.

## Ako to funguje
- **Heslo → kľúč:** PBKDF2-SHA-256, 600 000 opakovaní, náhodná soľ. Heslo aspoň 12 znakov; nikde sa neukladá.
- **Šifrovanie:** AES-256-GCM po kúskoch (~256 kB), každý s vlastným nonce, viazaný na hlavičku, svoje poradie a na
  to, či je posledný – zmenený, vymenený, chýbajúci alebo odrezaný kúsok sa zistí. Len kryptografia .NET, vlastný formát.
- **Značka:** každý trezor má značku `private:<meno>` (napr. `private:intercorp`). Čo sa z neho naučí (adaptér), sa
  **zapečatí tým istým kľúčom a značkou** (`.sealed`) – otvorí sa tým istým heslom.
- **Časť len na meranie:** 2 % párov (vyberá ich odtlačok páru, teda vždy tie isté) sa **nikdy neučia** – na nich sa
  meria chrF súkromného adaptéra.
- **V pamäti:** dáta sa otvárajú len v pamäti, kúsok po kúsku, a kúsok sa po použití prepíše nulami. Kľúč je v
  pripnutej pamäti a po použití sa zmaže.

## Stena
- Server učebne **odmietne** štartovací model alebo model, ktorý je trezorom, zapečateným súborom alebo má v hlavičke
  značku súkromného učenia (`Vault.HoldsPrivate`).
- Zapečatený adaptér sa **nedá načítať ako obyčajný** (aplikácia, Učebňa, server) – len príkazmi nižšie s heslom.
- Preklad so súkromným adaptérom **nezapisuje do pamäte prekladov** (tá slúži aj verejnému učeniu) a vypíše
  upozornenie, že preklady sú len pre vlastníka a nikdy nie na učenie verejného modelu.
- CI pri každom PR a vydanie pred zverejnením skontrolujú (`tools/ci/no-private.sh`), že v repozitári ani vo vydaní
  nie je trezor ani súbor so značkou súkromného učenia. `.irisvault` a `.sealed` sú v `.gitignore`.

## Príkazy
```
iris vault create <páry.tsv> <dáta.irisvault> --tag intercorp     # heslo 2×; pôvodný súbor sa nemení
iris vault info <dáta.irisvault>                                  # druh, značka, kedy (bez hesla)
iris vault check <dáta.irisvault>                                 # celý ho otvorí a spočíta páry
iris learn <model.iris> <dáta.irisvault> <adaptér.sealed> [voľby] # učí sa z trezoru, adaptér zapečatí
iris score-vault <model.iris> <dáta.irisvault> --adapter <adaptér.sealed>   # chrF na časti len na meranie
iris translate <model.iris> --adapter <adaptér.sealed>            # preklad pre vlastníka (heslo)
```
Páry: jeden na riadok, `veta TAB preklad` (UTF-8).

## Čo trezor nezvládne sám – odporúčania
- **Pôvodný súbor** (stiahnutý korpus) trezor nemení ani nemaže (zásada: originály sa nemenia). Držte ho len na
  šifrovanom disku (BitLocker) alebo ho po vytvorení trezoru sami zmažte.
- Počas učenia sú vety v pamäti procesu ako text (.NET ich nevie spoľahlivo prepísať) a systém môže pamäť odložiť do
  stránkovacieho súboru. Preto: **BitLocker na disku** počítača, kde sa učí (šifruje aj stránkovací súbor), a
  súkromné učenie len na počítačoch, ku ktorým sa nikto iný nedostane.
- Heslo nezapisujte do súborov ani skriptov. Pri presmerovanom vstupe sa číta prvý riadok (napr. z bezpečného
  správcu hesiel), ale nikdy nie ako argument príkazu.
- Neskôr (dohodnuté): súkromné učenie aj na nevyužívaných PC v učebni s kľúčom len v RAM v noci – najprv informovať ČNK.
