# IRIS – učenie v noci v učebni

Počítače v učebni sa mimo vyučovania učia čítať text (OCR). Každý počítač sa učí na vlastných riadkoch textu,
ktoré si sám nakreslí. Server po každom kole spriemeruje, čo sa naučili, do jedného modelu. Všetko je vlastný kód IRIS
v čistom .NET, bez Pythonu a bez cudzích knižníc.

## Časti
- **`iris-hub`** – server (koordinátor) na stále zapnutom počítači so systémom Linux. Drží model, otvára kolá podľa
  času učenia a ukladá model po každom kole (aj kópiu do zdieľaného priečinka, ak je zadaný).
- **`iris-agent`** – služba Windows na počítačoch v učebni:
  - spúšťa sa s Windows pod systémovým účtom; žiak ju nezastaví ani nevypne;
  - po páde ju Windows znova spustí;
  - je viditeľná v *Službách* ako „IRIS – učenie v noci“.
- **Kľúče:**
  - `agent.key` – pre počítače v učebni;
  - `console.key` – len pre konzolu správcu.

  Každá správa je podpísaná. Počítač v učebni neotvára žiadny port, sám sa pripája na server. Vykoná len pevné
  príkazy: pozastaviť, pokračovať, vypnúť, reštartovať. Nikdy nespustí žiadny program ani skript.

## Skúška bez servera (napr. doobeda na niekoľkých PC)
```
iris-agent.exe test --minutes 10
```
- Učí sa 10 minút.
- Využíva polovicu jadier a pracuje polovicu času (`--cores 0.5`, `--duty 0.5`).
- Beží v **úspornom režime Windows** (EcoQoS, `--eco yes`): na procesoroch s úspornými jadrami ide práca na ne a jadrá
  bežia na najúspornejšej frekvencii – menej energie a tepla, tichšie ventilátory, o niečo pomalšie. Porovnanie:
  `--eco no`.
- Každých 10 s ukáže:
  - vyťaženie procesora (celé PC aj samotné učenie);
  - pamäť;
  - teplotu, ak ju počítač ponúka;
  - počet riadkov za sekundu.

Iné nastavenie: `--duty 0.3` (tichšie), `--cores 0.25`, `--eco no`. Počas skúšky si vypočujte ventilátory.

## Server (Debian alebo Ubuntu, bez grafiky)
Server je jeden program (`iris-hub`) a jedna služba systemd. Učí ho vlastný používateľ `iris-hub` bez prihlásenia,
nie root. Zo systému nemení nič iné: **Samba, jej nastavenia a zdieľané priečinky ostanú nedotknuté**, nič sa
neinštaluje z internetu (okrem písiem, len ak o ne požiadate). Firewall upraví len vtedy, keď beží `ufw`: pridá jedno
pravidlo pre port 47620.

### Inštalácia
1. Na svojom PC stiahnite **`iris-ucebna-linux`** (GitHub → Actions → posledný beh CI → Artifacts) a rozbaľte ho do
   zdieľaného priečinka servera. Po vydaní IRIS je to súbor `iris-hub-<verzia>-linux.tar.gz` medzi súbormi vydania.
2. Na serveri (cez SSH) v tom priečinku:
   ```
   sudo bash install.sh --backup <cesta k zdieľanému priečinku na serveri>
   ```
   Ďalšie voľby:
   - `--start <iris-ocr-train.iris>` – model, z ktorého sa začne;
   - `--fonts` – doinštaluje voľné písma Debianu, s ktorými server meria, ako model číta;
   - `--port N` – iný port;
   - `--options "…"` – ostatné nastavenia, napr. `"--round 10 --from 15:00 --until 06:00"`.
3. Skript nainštaluje:
   - program do `/opt/iris-hub`;
   - dáta do `/var/lib/iris-hub` (model, kľúče, história – číta ich len server);
   - nastavenia do `/etc/default/iris-hub`.

   Potom server spustí a vypíše, či beží. Opätovné spustenie skriptu aktualizuje program a dáta ponechá.
4. **Kľúče** si odneste a hneď ich zo zdieľaného priečinka zmažte:
   ```
   sudo cp /var/lib/iris-hub/agent.key /var/lib/iris-hub/console.key <zdieľaný priečinok>/
   ```
   - `agent.key` patrí na počítače učebne;
   - `console.key` len do konzoly.
- **Zdieľaný priečinok:** ak ho skript vytvára, patrí serveru. Ak už existuje, server dostane len právo doň zapisovať
  (ACL, balík `acl`). Vlastník ani ostatné práva sa nemenia. Súbory, ktoré server zapíše, sú čitateľné pre všetkých
  (aj cez Sambu).
- **Denník:** `journalctl -u iris-hub -f`.
- **Zmena nastavení:** upravte `/etc/default/iris-hub` a spustite `sudo systemctl restart iris-hub`.
- **Predvolené nastavenia:**
  - kolo trvá 10 minút;
  - učí sa 15:00–6:00, cez víkendy celý deň;
  - počítače pracujú polovicu času na polovici jadier, v úspornom režime Windows (`--eco no` ho vypne);
  - port je 47620.
- **Súbory:** model v `lab-model.iris`, čítačka pre IRIS v `iris-ocr.iris`, kolá v `rounds.jsonl`.

### Aktualizácie (podpísané)
- **Server sa raz denne okolo poludnia pozrie na GitHub**, či vyšla nová verzia IRIS (`iris-hub-update.timer`).
- **Inštaluje len programy podpísané kľúčom vydaní IRIS.** Súkromná časť kľúča je len tajomstvom vo vydávacom procese
  na GitHube. Každý súbor má v podpísanom zozname svoj SHA‑256. Inak nenainštaluje nič.
- **Po stiahnutí:**
  - nový program servera dá na miesto a reštartuje službu;
  - programy pre počítače a konzolu uloží k sebe.
- **Počítače učebne** dostanú nový program od servera, nikdy nie z internetu. Server im ho dáva sám medzi kolami
  (`--auto-update no` to vypne). Konzola to vie aj hneď a hromadne. Počítač podpis overí znova, vymení svoj program
  a Windows službu znova spustí.
- **Konzola** ukáže, že je nová verzia, a jedným tlačidlom sa aktualizuje.
- **Hneď (nečakať do poludnia):**
  - `sudo systemctl start iris-hub-update`;
  - alebo tlačidlo „Aktualizovať server“ v konzole.
- **Kontrola bez inštalácie:** `sudo /opt/iris-hub/iris-hub update --check`.
- Nová verzia vzniká len vydaním, ktoré schvaľuje vlastník (GitHub → Actions → Release → Review deployments).

**Kľúč vydaní – raz, robí ho vlastník (súkromná časť nikdy neopustí jeho stroj a GitHub):**
1. Na serveri: `/opt/iris-hub/iris-hub signing-key --out ~/iris-update-key.pem`.
2. Celý text súboru vložte na GitHube do tajomstva **`IRIS_UPDATE_KEY`**: Settings → Secrets and variables → Actions →
   New repository secret.
3. Súbor zmažte, alebo ho uložte offline ako zálohu.
4. Verejnú časť, ktorú príkaz vypíše, treba v repozitári uložiť ako `tools/lab/update-key.pub`. Vstavia ju programy
   z nasledujúcich verzií.
5. Kým kľúč nie je nastavený, vydanie programy učebne neobsahuje a nič sa samo neaktualizuje.

### Odinštalovanie
```
sudo bash /opt/iris-hub/uninstall.sh            # služby, program, nastavenia; dáta zostanú
sudo bash /opt/iris-hub/uninstall.sh --purge    # aj dáta (model, kľúče, história) a používateľ iris-hub
```
Zdieľaný priečinok so zálohou modelu ani nič iné v systéme sa pri odinštalovaní nemení.

## Konzola
- `iris-console.exe` na vašom PC. V *Nastaveniach* zadajte server a kľúč `console.key`. Konzola má tieto stránky:
  - **Prehľad** – dlaždice počítačov podľa stavu, vyťaženia alebo teploty; pravým tlačidlom príkazy.
  - **Počítače** – tabuľka všetkých meraní.
  - **Grafy** – história za 6 h až týždeň.
  - **Kolá a učenie** – loss a chybovosť (CER).
  - **Sieť** – prehľadanie siete a budenie.

## Budenie a vypínanie
- **Server o 15:00 zobudí počítače učebne, ktoré sú vypnuté** (Wake-on-LAN) a **o 6:00 ich zasa vypne**. Tie, ktoré
  boli zapnuté, nechá tak.
- **Na každom PC treba v BIOS/UEFI povoliť Wake-on-LAN** (napr. „Power On by PCI-E/LAN“) a vo Windows na sieťovej karte
  „Umožniť tomuto zariadeniu prebudiť počítač“. Rýchle spustenie Windows môže budenie z vypnutého stavu brániť.
- Kartu (MAC) každého PC si server zapamätá, keď sa PC prvýkrát pripojí.
- Vypnúť automatické budenie: `iris-hub run … --wake no`.
- **Prehľadanie siete** (stránka Sieť) robí server, len vo vlastnej sieti. Zisťuje odozvu (ping), názov NetBIOS
  (odpovedá naň aj Windows, ktorý neodpovedá na ping), kartu z tabuľky ARP a názov v DNS. Porty neskúša.

## Počítač v učebni (ako správca)
```
iris-agent.exe install --hub <adresa servera> --key agent.key
iris-agent.exe uninstall
```
- **Inštalácia:**
  - program sa nakopíruje do `Program Files\IRIS Lab`;
  - nastavenia a kľúč idú do `ProgramData\IRIS Lab` – tento priečinok smie otvoriť len systém a správcovia, žiak kľúč
    neprečíta;
  - vytvorí sa služba `IRIS-Lab`.
- **Denník** je v `ProgramData\IRIS Lab\agent.log`.

## Ako sa učenie správa k počítaču
- **Učenie ustúpi**, keď počítač robí niečo iné (iné programy berú viac ako 35 % procesora).
- **Beží s nižšou prioritou**, takže čokoľvek iné má prednosť.
- **Teplota:** IRIS ju číta zo zón ACPI cez výkonnostné počítadlá Windows. Ak ich doska nemá, teplota sa neukáže.
  Žiadny ovládač sa neinštaluje.
