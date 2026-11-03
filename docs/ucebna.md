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

## Server
```
iris-hub keys --folder <priečinok servera>      # vytvorí agent.key a console.key
iris-hub run --folder <priečinok servera> --start <iris-ocr-train.iris> --backup <zdieľaný priečinok>
```
- Predvolené nastavenia:
  - kolo trvá 10 minút;
  - učí sa 15:00–6:00, cez víkendy celý deň;
  - počítače pracujú polovicu času na polovici jadier, v úspornom režime Windows (`--eco no` ho vypne);
  - port je 47620.
- Ako služba systemd: `tools/lab/iris-hub.service`.
- Model v `lab-model.iris`, čítačka pre IRIS v `iris-ocr.iris`, kolá v `rounds.jsonl`.

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
