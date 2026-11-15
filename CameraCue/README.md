# CameraCue 0.1.4

CameraCue ist eine eigene Firmware fuer den **M5StickC Plus2** und nutzt **Tally Arbiter** als Backend.

## Anzeige

- **Innenflaeche:** Zustand der physischen Kamera im ATEM
  - rot = Program / LIVE
  - gruen = Preview
  - blau = Safe
  - grau = UNKNOWN / Verbindung oder Mapping fehlt
- **Aussenbereich:** Zustand des ATEM-Feeds in vMix
  - rot = Program / LIVE
  - gruen = Preview
  - blau = Safe
  - grau = UNKNOWN
- Mitte: Regieanweisung / Cue
- oben: optional `next: ...`

## Neu in 0.1.4: Stabilitaets- und Recovery-Update

0.1.4 haertet die in 0.1.3 eingefuehrte Konfigurationsverwaltung und die Netzwerkverbindung. Wichtigste Aenderungen:

- WLAN-Powersave ist vollstaendig deaktiviert und der ESP32 laeuft fuer mehr Netzwerkreserve mit 160 MHz.
- WLAN-Verlust und Tally-Arbiter-Verlust werden getrennt behandelt und sichtbar angezeigt.
- Ein TA-Reconnect-Watchdog baut eine haengende Socket.IO-Verbindung automatisch neu auf.
- Nach WiFi-Reconnect registriert sich CameraCue neu und holt Metadaten **inklusive aktuellem `device_states`** erneut ab.
- Tally-Arbiter-Systemmeldungen mit Nachrichtentyp `server` werden nicht mehr als Kamera-Cue angezeigt.
- Bei nicht erreichbarem gespeichertem WLAN startet automatisch der nicht-destruktive Setup-AP, statt lange in einem Portal festzuhaengen.
- WLAN-Wechsel/Rollback wurde korrigiert: gespeicherte SSID ist auch offline sichtbar, leeres Passwort behaelt das alte Passwort; offene Netze sind explizit waehlbar.
- Der fruehere `setHoldThresh()`-Pfad wird gar nicht mehr benutzt.

## Einstellungen bearbeiten, ohne Reset

### 1. Weboberflaeche im normalen WLAN

Wenn der Stick mit dem WLAN verbunden ist, die in der Diagnoseansicht angezeigte Adresse oeffnen, z. B.:

```text
http://192.168.178.181/
```

Dort koennen geaendert werden:

- WLAN-SSID und Passwort
- Tally-Arbiter-IP/Hostname und Port
- optionaler ATEM Source Hint
- optionaler vMix Source Hint
- Displayhelligkeit
- Buzzer

Nach dem Speichern startet CameraCue kontrolliert neu. Das ist **kein Factory Reset**; Device-Zuordnung und andere Einstellungen bleiben erhalten.

### 2. Setup-AP ohne Loeschen

**A+B etwa 3 Sekunden halten.** CameraCue startet zusaetzlich einen Access Point:

```text
SSID: CameraCue-XXXXXX-Setup
Passwort: cameracue
Adresse: http://192.168.4.1/
```

Der normale gespeicherte Zustand wird dabei nicht geloescht. Das ist besonders hilfreich, wenn die bisherige Tally-Arbiter-Adresse nicht mehr stimmt.

### 3. Factory Reset ist absichtlich schwerer

**A+B etwa 10 Sekunden halten.** Danach erscheint eine Bestaetigung:

- A = Factory Reset wirklich ausfuehren
- B = abbrechen

Erst der bestaetigte Factory Reset loescht WLAN und CameraCue-NVS.

### 4. USB / Serial CLI

Serial Monitor mit 115200 Baud oeffnen. Befehle:

```text
help
status
config
setup
reboot
set ta 192.168.178.127 4455
set wifi MeineSSID MeinPasswort
```

Hinweis: Fuer SSIDs mit Leerzeichen bitte die Weboberflaeche verwenden.

## Sicherer WLAN-Wechsel

Wenn WLAN-Daten ueber die Weboberflaeche oder CLI geaendert werden, speichert CameraCue vorher die bisherigen Credentials. Nach dem Neustart werden die neuen Daten bis zu 20 Sekunden getestet. Scheitert der Test, versucht CameraCue automatisch das vorherige WLAN wiederherzustellen. Falls auch das nicht funktioniert, startet CameraCue automatisch den nicht-destruktiven `CameraCue-XXXXXX-Setup`-AP.

## Verbindungsstabilitaet

0.1.4 behandelt WLAN und Tally Arbiter getrennt:

1. Bei WLAN-Verlust werden ATEM und vMix sofort UNKNOWN/grau und `WIFI OFF` angezeigt.
2. CameraCue versucht periodisch, das gespeicherte WLAN wiederherzustellen.
3. Nach `GOT IP` wird Socket.IO sauber neu aufgebaut und der Listener erneut registriert.
4. Wenn WLAN stabil bleibt, aber Tally Arbiter/Socket.IO offline ist, erzwingt ein 8-Sekunden-Watchdog einen Neuaufbau.
5. Neben Sources/Device Sources/Bus Options wird periodisch auch der aktuelle `device_states`-Stand erneut angefordert.

Die Diagnose zeigt RSSI, WiFi-Reconnects, TA-Reconnects, Socket-Disconnects und die letzte Fehlerursache.

## Tally-Arbiter-Konfiguration

Fuer jede physische Kamera ein Device anlegen, z. B. `Kamera 1`, und diesem zwei Device Sources zuordnen:

1. Blackmagic ATEM -> echter Kameraeingang
2. vMix -> Input, auf dem der ATEM-Ausgang liegt

Bei allen drei Kamera-Devices darf dieselbe vMix-Adresse verwendet werden. Preview/Program fuer diesen Anwendungsfall nicht zusammenlinken, weil CameraCue die Source-Beitraege getrennt auswertet.

CameraCue erkennt die Source-Typen anhand der Tally-Arbiter-Type-IDs:

- Blackmagic ATEM: `44b8bc4f`
- vMix: `58b6af42`

## Pager-Nachrichten

Unterstuetzt:

```text
[CUE]Bitte etwas hoeher
[CLEAR]
[NEXT]Predigt
[NEXT:CLEAR]
[CLEARALL]
```

Normale Producer-Nachrichten bleiben kompatibel. Bekannte Tally-Arbiter-Systemmeldungen (z. B. Listener Connect/Disconnect oder Test Mode) werden in 0.1.4 nicht mehr als grosse Kameraanweisung angezeigt.

## Bedienung

- A kurz: Haupt-/Diagnoseansicht
- B kurz: Helligkeit
- A+B 3 s: nicht-destruktiver Setup-AP
- A+B 10 s: Factory-Reset-Bestaetigung

OTA:

- Hostname: `CameraCue-XXXXXX`
- Passwort: `cameracue`

## Build / Upload

VS Code + PlatformIO:

1. Projektordner oeffnen.
2. M5StickC Plus2 per USB-C anschliessen.
3. PlatformIO Build ausfuehren.
4. PlatformIO Upload ausfuehren.
5. Optional Serial Monitor bei 115200 Baud.

Oder die mitgelieferten Dateien verwenden:

```text
build.cmd
upload.cmd
monitor.cmd
```

Falls `pio` in PowerShell nicht im PATH liegt:

```powershell
& "$env:USERPROFILE\.platformio\penv\Scripts\platformio.exe" run
```

## Sicherheitslogik

**Grau bedeutet UNKNOWN und nicht SAFE.** Ein Netzwerkausfall darf niemals wie ein sicherer blauer Zustand aussehen.

## Build-Hinweis

Das Projekt enthaelt weiterhin die lokale `boards/m5stick-cplus2.json`, den 0.1.2-Button-Crashfix und den `DFRobot_GP8XXX`-Ignore. In der Chat-Ausfuehrungsumgebung steht kein vollstaendiger ESP32/PlatformIO-Build-Stack zur Verfuegung; deshalb muss der erste echte Build von 0.1.4 weiterhin auf dem lokalen PlatformIO-System validiert werden.
