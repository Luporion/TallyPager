# TallyPager 0.8.0

Tally und Chat für mobile Kameras mit dem M5StickC Plus2. Die Windows-App kann ATEM und vMix direkt lesen oder Tally Arbiter als Quelle verwenden. CameraCue 0.1.4 bleibt als eigener [Build für Tally Arbiter](CameraCue/README.md) verfügbar.

## Start und Einrichtung

1. Für die portable Variante `TallyPager-Windows-x64.exe` und `TallyPager-Firmware-M5StickC-Plus2-0.8.0.zip` aus `dist/0.8.0/` in denselben Ordner legen. Für die installierbare Variante `TallyPager-Setup-Windows-x64.exe` starten. Setup installiert die App samt Firmwarepaket für das Windows-Benutzerkonto und legt einen Startmenüeintrag an. Auch spätere Setup-Versionen aktualisieren diese Installation. Die App läuft ab Windows 10 Version 2004 und benötigt keine separate .NET-Installation.
2. Unter **Stick einrichten/verwalten → USB** den Port, die **Adresse des Basis-Servers** im Kameranetz und den Sticknamen wählen. Firmwareversion und Funktionen lassen sich mit **Firmware lesen** prüfen. **Firmware aktualisieren** verwendet das separate ZIP und prüft vor dem Flashen Modell, Version und Prüfsummen. **Stick einrichten** verwendet die bereits installierte Firmware, soweit sie den gewählten Modus unterstützt.
3. WLAN-Name und Passwort eingeben oder beide Felder für das gespeicherte WLAN leer lassen. Der Stick benötigt ein 2,4-GHz-WLAN und muss den PC per UDP auf Port 46224 erreichen.
4. Unter **Externe Verbindungen → Mixer und Zuordnung** die gewünschten Quellen verbinden. Einen Stick in der Tabelle auswählen, pro Ebene Quelle und Eingang wählen und die Zuordnung speichern. **Aus · ausblenden** entfernt Farbe und Quellzeile für diese Ebene. **Innen und außen tauschen** speichert den Tausch sofort. Der Stick zeigt die zugeordnete Quelle an, zum Beispiel `ATEM: LIVE`.
5. Unter **Regie** einen oder mehrere Sticks auswählen. **Enter** sendet den Inhalt der gerade bearbeiteten Zeile. Jede Zeile hat **Leeren** und **Senden**. **Chat und Next senden** überträgt beide Felder. Einzelaktionen erhalten den jeweils anderen Text pro Stick.

Für die Kaskade kann pro Stick innen sein physischer ATEM-Eingang und außen der in vMix eingespeiste ATEM-Ausgang gewählt werden. Bei direkter vMix-Kamera kann der vMix-Eingang auf jede gewünschte Ebene gelegt werden. Fehlende Quellen oder Zuordnungen zeigen Grau, bestätigter inaktiver Zustand Blau, Preview Grün, Program Rot.

### vMix verbinden

- Unter **Settings → Web Controller** muss für den TCP-Weg **TCP API → Enabled** aktiv sein. Läuft TallyPager auf dem vMix-PC, unter **Externe Verbindungen → vMix** `127.0.0.1` eintragen. Ein Hostname oder eine IP aktiviert TCP auf Port 8099. Falls `SUBSCRIBE TALLY` ausbleibt oder abgelehnt wird, fragt TallyPager `TALLY` und `XML` direkt ab. [vMix TCP API](https://www.vmix.com/help30/TCPAPI.html)
- Mit `http://127.0.0.1:8088` im selben Feld verwendet TallyPager den Web Controller. Den Port aus vMix übernehmen und für lokalen Zugriff **Allow software on this computer access without login** aktivieren. Auf einem zweiten PC die erreichbare Web-Adresse des vMix-PCs und die API-Zugriffsfreigabe in vMix prüfen. Die Web-API zeigt Program, Preview und die gemeldeten Overlays aus dem aktuellen XML-Zustand. [vMix Web Controller](https://www.vmix.com/help30/WebController.html) · [vMix HTTP Web API](https://www.vmix.com/help30/DeveloperAPI.html)

## Updates über GitHub

1. **Updates** oben im Programm öffnen. Die App fragt das als neuestes stabile Release markierte GitHub-Release ab.
2. **Programm und Firmware laden** lädt die Windows-EXE und das passende Firmware-ZIP gemeinsam. Die App prüft beide SHA-256-Prüfsummen, die EXE-Version und das Firmware-Manifest. Sie legt die Dateien in einem eigenen Versionsordner ab und zeigt dessen Pfad an. Die bisherige EXE bleibt als Rückfall verfügbar.
3. **Neue Version starten** beendet die bisherige App und öffnet die neue. Regieprofile und Pairings liegen weiterhin im Windows-Benutzerprofil. Die neue App wählt das neben ihr liegende Firmware-ZIP automatisch aus.
4. Den gewünschten M5StickC Plus2 per USB verbinden und unter **Stick einrichten/verwalten → USB → Firmware aktualisieren** auswählen. Ein Firmware-Update startet auf deine Bestätigung.

Der Updater lädt aus den öffentlichen [TallyPager-Releases](https://github.com/Luporion/TallyPager/releases). Der GitHub-Workflow veröffentlicht zu einem passenden stabilen Git-Tag beide Dateien samt Prüfsummen. Der erste Build mit Updater wird einmal als EXE geöffnet.

## Flotteneinrichtung und Geräteprofile

- **Stick einrichten/verwalten → USB** meldet hinzugekommene und getrennte COM-Ports. **Stick auslesen** liest ab Firmware 0.7.0 die gespeicherten Daten über USB. Das Passwort ist im Dialog verdeckt und lässt sich mit **Anzeigen** einblenden.
- **WLAN-Daten für weitere Sticks behalten** lässt die Eingaben nach erfolgreicher Einrichtung stehen. Mit **Stick-Nummer nach erfolgreicher Einrichtung erhöhen** folgt auf `Stick 1` der Name `Stick 2`. **Aktuelles merken** speichert WLAN-Daten explizit und verschlüsselt im Windows-Konto. Eine **Einrichtungsvorlage** merkt sich die übrigen USB-Felder und verweist auf das gespeicherte WLAN. Regieprofile speichern dagegen den Aufbau in der Regie mit Quellen und Kamerarollen.
- Auf jedem M5StickC Plus2 können durch USB-Einrichtung bis zu vier **Geräteprofile** vorbereitet werden. Jedes Profil speichert auch WLAN, Core-Adresse und Funkmodus. Beide Tasten rund zwei Sekunden halten, beide loslassen und mit **A** das Profil wählen. **B** bestätigt den Wechsel. Das Gerät startet mit dem gewählten Profil neu.
- Unter **Regie → Stick-Vorschau anzeigen** zeigt die App für den ausgewählten Stick ihren letzten Anzeigezustand und dessen Bestätigung. Eine USB-Funkbasis mit Firmware 0.7.0 zeigt bis zu vier über ihren Modus verwaltete Pager. Der Core überträgt die lokale PC-Uhrzeit über den aktiven Tally-Funkweg.

### Funkmodus ohne USB wechseln

1. Den Pager einmal per USB auf Firmware **0.8.0** aktualisieren. Unter **Stick einrichten/verwalten → USB** pro gewünschtem Funkmodus ein anderes **Geräteprofil am Stick** speichern. Der Stick startet nach jedem Speichern im gerade bearbeiteten Profil. Der zuletzt eingerichtete Modus dient als Ausgangspunkt.
2. Die Verbindungen für beide Modi bereitstellen. Bei WLAN + UDP muss der Stick den Basis-Server im gespeicherten Netz erreichen. Bei BLE **Einstellungen Funkbasis-Modus → BLE direkt verbinden** oder eine USB-BLE-Funkbasis verwenden. Bei ESP-NOW und ESP-NOW LR müssen Adresse, Kanal und Modus der USB-Basis zum Zielprofil passen.
3. Oben den Stick auswählen, **Stick einrichten/verwalten → Funkmodus wechseln** öffnen, das gewünschte **Geräteprofil** wählen und **Ausgewählten Stick umstellen** drücken. **Alle erreichbaren Sticks umstellen** wechselt eine Flotte zu einem Zielmodus. Sticks mit mehreren passenden Profilen lassen sich einzeln umstellen. Die Ergebnisliste zeigt für jeden betroffenen Stick den bestätigten Wechsel, einen Rückfall oder die Ursache, warum er übersprungen wurde.
4. Der Pager startet neu und wartet bis zu 30 Sekunden auf Tally vom authentifizierten Basis-Server. Wenn es ankommt, übernimmt er das Profil dauerhaft. Sonst startet er wieder im vorherigen Profil. Die Funkbasis bleibt für ESP-NOW und ESP-NOW LR separat unter **Einstellungen Funkbasis-Modus** umstellbar.

## Funkmodi und WLAN-Verwaltung

| Modus | Aufbau |
| --- | --- |
| **WLAN + UDP** | Verwendet die Windows-Netzwerkadapter. Stick und Basis-Server brauchen ein erreichbares lokales Netz. UDP 46224 trägt Tally, Chat, Status und Quittierungen. |
| **ESP-NOW** | Ein zweiter M5StickC Plus2 bleibt per USB als Funkbasis am Regie-PC. Basis und Pager brauchen denselben Kanal und der Pager die Adresse der Funkbasis. |
| **ESP-NOW LR** | Gleicher Aufbau mit dem Espressif-LR-Modus auf beiden Geräten. Die Reichweite im Saal muss gemessen werden. |
| **BLE** | Windows-Bluetooth-LE-Adapter oder USB-BLE-Funkbasis. Unter **Einstellungen Funkbasis-Modus → BLE direkt verbinden** sucht und verbindet die App BLE-Pager über den Windows-Adapter. Eine ESP-NOW-Funkbasis und WLAN-Pager können parallel laufen. |

Die eigenständige Windows-App nutzt die obigen Modi. Die ältere CameraCue-Firmware mit Tally Arbiter nutzt WebSocket und bleibt als eigenes Paket verfügbar.

In der Tabelle zeigt **Adresse** die IPv4-Adresse bzw. die Bluetooth- oder ESP-NOW-Adresse des Sticks. Bei getrennten Geräten bleibt die letzte bekannte Adresse sichtbar. Unten rechts stehen die aktiven Netzwerkadapter des Basis-Servers und ihre IPv4-Adressen. Ein Klick zeigt die komplette Liste inklusive IPv6 und inaktiver Adapter.

1. **Einstellungen Funkbasis-Modus** öffnen, den USB-Port der Basis und ESP-NOW, ESP-NOW LR oder BLE auswählen. **Stick als Funkbasis einrichten**, anschließend **Funkbasis verbinden**. Dieselbe Firmware-ZIP enthält beide Rollen. **Wieder als Pager starten** übernimmt die vorher auf dem Gerät gespeicherte Pager-Konfiguration, sofern eine vorhanden ist.
2. Einen weiteren Stick für die Kamerarolle per USB verbinden. Unter **Stick einrichten/verwalten → USB** denselben Funkmodus wählen. Bei ESP-NOW und ESP-NOW LR übernimmt die App von einer angeschlossenen Basis deren Adresse und Kanal in die USB-Einrichtung. Bei BLE findet die Basis den Pager automatisch. Eine USB-Basis kann einen Modus gleichzeitig bedienen. WLAN-Pager können parallel verbunden bleiben.
3. **Stick einrichten/verwalten → WLAN** funktioniert mit bereits gepaarten Sticks im Modus **WLAN + UDP**. Den Stick oben auswählen und **Einstellungen laden** anklicken. Wenn die Tally-Verbindung fehlt, zuerst **Im WLAN suchen** wählen. Bei gesperrtem Broadcast die Stick-IP aus der Diagnoseansicht eingeben. Die Suche und der direkte Zugriff verwenden UDP 46226 auf dem Stick.
4. Name, PC-Adresse oder WLAN-Zugangsdaten aktualisieren. Ein leeres WLAN-Feld behält das bisherige WLAN. Das Passwort wird bei der Übertragung verschlüsselt und beim Laden der Einstellungen nicht angezeigt. Neue Netzwerkdaten probiert der Stick bis zu 30 Sekunden aus und speichert sie erst nach Wiederaufbau einer authentifizierten Tally-Verbindung. Bei Fehlschlag stellt er die vorherige Verbindung wieder her. Firmware-Update, Erstpairing und Wechsel des Funkmodus erfolgen per USB.

Für den Vergleich kann die Regie den aktiven Funkmodus und die RTT in der Stick-Tabelle ablesen. RSSI bleibt auf WLAN beschränkt. Den gleichen Laufweg und dieselbe Simulation je Modus verwenden. Die RTT misst den Rundlauf des Protokolls. Die sichtbare Umschaltzeit am Display separat erfassen. [Espressif ESP-NOW für ESP-IDF 4.4](https://docs.espressif.com/projects/esp-idf/en/v4.4.8/esp32/api-reference/network/esp_now.html) · [Espressif Wi-Fi Long Range](https://docs.espressif.com/projects/esp-idf/en/v4.4.8/esp32/api-guides/wifi.html)

### Tally Arbiter

Unter **Externe Verbindungen → Tally Arbiter** den Host, `Host:Port` oder die HTTP-/HTTPS-Adresse des laufenden Servers eingeben und verbinden. Ein reiner Host verwendet Port 4455. Danach für die Ebene **Tally Arbiter · Gerät**, **Tally Arbiter · ATEM** oder **Tally Arbiter · vMix** wählen und das gewünschte Gerät aus dessen Liste übernehmen. **Gerät** wertet die aktiven Beiträge aller erreichbaren, zugeordneten Quellen aus. **ATEM** und **vMix** filtern auf den jeweiligen Mischertyp. Programm hat Vorrang vor Preview. Die App liest Tally und Gerätezuordnungen über Socket.IO. Chat und Next kommen weiterhin aus der Windows-App.

### Firmware-Kompatibilität

Die Spalte **Firmware** zeigt pro Stick die gemeldete Version. Beim Auswählen zeigt die App fehlende Funktionen und Funktionskennungen, die sie noch nicht kennt. Updates sind separat startbar. Firmware **0.6.0** funktioniert weiterhin. Wenn ein ungeprüfter 0.6.0-Pager nur per ESP-NOW oder BLE verbunden ist, kann die App seine genaue Version erst über **Firmware lesen** per USB ermitteln. Der Funkstatus zeigt bis dahin eine Mindestversion an.

Ab **0.6.1** meldet jeder Stick seine Version und optionalen Funktionen über USB und im authentifizierten Paket `TP1|firmware|Gerät|Sitzung|Version|Funktionen` über alle Tally-Transporte und bei der WLAN-Suche. `Version` besteht aus drei Zahlen. `Funktionen` ist eine durch Kommas getrennte Liste stabiler Kennungen. Bei späteren Releases bleiben die bestehenden TP1-Pakete und USB-Befehle kompatibel. Neue optionale Funktionen erhalten neue Kennungen. Clients zeigen unbekannte Kennungen an und verwenden weiterhin die bekannten Funktionen, die das Gerät anbietet.

## Regieprofile, Sicherung und Schnelltexte

- Beim ersten Start übernimmt **Standard** die bisherigen Gerätezuordnungen und die gespeicherte Tally-Arbiter-Adresse. Die App verwendet lokal DPAPI im Windows-Benutzerkonto für Geräteschlüssel und das Companion-Token. Nach der Schlüsselmigration benötigt ein PC-Rollback auf 0.4.0 erneutes Pairing.
- Im Tab **Regieprofile** ein Profil kopieren, auswählen und mit **Aktivieren** übernehmen. Das Profil speichert Quellenadressen samt Verbindungswahl, Kamera-Rollen, beide Tally-Ebenen, Zielgruppen und Schnelltexte. Der Profilwechsel beendet laufende Regietexte und Simulationen. Im neuen Aufbau stellen die im Profil aktivierten Quellen ihre Verbindungen wieder her.
- **Profil exportieren** gibt den gerade aktiven Aufbau weiter. **Sicherung exportieren** sichert alle Profile. Der Import bietet eine Vorschau, neue Kopien oder Ersatz des vorhandenen Aufbaus mit automatischer lokaler Vorsicherung. Portable Backups enthalten Profile samt Vorlagen. Am Ersatz-PC die Sticks per USB einrichten und zu den importierten Rollen binden. Formatdetails stehen im [JSON Schema](docs/profile-backup.schema.json).
- In **Regieprofile** einen Schnelltext mit Chat und/oder Next anlegen und Rollen oder eine Gruppe wählen. In **Regie** lässt er sich direkt senden oder zur Bearbeitung in die Felder übernehmen. Nutze **Chat-Quittierung anfordern** auch beim manuellen Senden. Die Standardfrist dafür beträgt 120 Sekunden. Ein M5Stick mit 0.5.0 quittiert mit **B** im Hauptbild. Die Regie sieht in der Spalte **Chat** unter anderem **Ausstehend**, **Gesendet**, **Empfangen** und **Quittiert**. **Diagnose** zeigt abgeschlossene Anweisungen.

## Bitfocus Companion und Middle Control

1. Companion und TallyPager auf demselben Windows-PC starten. Unter **Externe Verbindungen → Bitfocus Companion und Middle Control** die **Lokale API starten** und den Authorization-Wert kopieren. Für die Stream-Deck-Nutzung kann die API bei jedem Programmstart automatisch angehen.
2. In Bitfocus Companion eine Verbindung **Generic HTTP** anlegen. Für eine Schnelltext-Aktion **POST** und die **POST-URL** aus TallyPager eintragen. Als Header `Authorization` setzen und den kopierten Wert einschließlich `Bearer` einfügen. Der POST braucht keinen Body. Weitere Aufrufe und Rückgaben stehen in der [OpenAPI-Datei](docs/companion.openapi.yaml).
3. Für Middle Control 4.2.0 unter Windows in Companion zusätzlich die Verbindung **Middle Things Middle Control** auf TCP **11580** konfigurieren. Auf einem Stream-Deck-Taster das Middle-Control-Preset für die richtige Camera ID und die Generic-HTTP-Aktion für den TallyPager-Schnelltext hinterlegen. Die optionale Middle Camera ID im Profil hilft bei der Zuordnung. Das Tally liest die App weiterhin aus den ausgewählten Mixern. [Middle Things Companion-Anleitung](https://www.middlethings.co/support/docs/joysticks-and-controllers/bitfocus-companion/)

## Simulation und Verbindungsanzeige

**Regie → Regiecheck** prüft das aktive Profil auf erreichbare Mixerquellen, gültige Eingangszuordnungen, gebundene und erreichbare Sticks sowie Firmware und USB-Funkbasis. Für ein sichtbares Prüfsignal zuerst genau einen erreichbaren Stick in der Tabelle auswählen und im Regiecheck **Tally-Test (6 s)** drücken. Nur dieser Stick zeigt die mit **TEST** gekennzeichneten Farben. Danach zeigt er wieder die aktuellen Mixerzustände. Der Test schaltet am Mixer nichts um.

- Die Simulation läuft nur für die ausgewählten Sticks. Sie bietet manuelle Zustände, vMix Program ↔ Inaktiv, vMix Program ↔ Quellausfall, wechselndes Program/Preview, ATEM Preview → Program → Inaktiv und eine Unterbrechung des Tally-Versands zum Stick. In den Intervall-Szenarien ist das Intervall die Dauer eines Schritts. Für die Anzeige beider Ausfallstufen am Stick und am PC eignen sich im Unterbrechungsszenario mindestens fünf Sekunden.
- Die simulierte Anzeige ist auf dem Stick mit **TEST** und am PC mit **Simulation** markiert. **Auswahl stoppen** oder **Alle stoppen** stellt die aktuellen Mixerwerte wieder her. Mit der neuen Firmware testen, damit die Markierung auf dem Stick erscheint.
- Der Stick zeigt oben rechts WLAN-Balken und RTT. **Verbunden** erscheint nach dem Aufbau der Verbindung für etwa zwei Sekunden. **Delayed** und **Getrennt** bleiben sichtbar, solange der Zustand anhält. Der PC zeigt pro Stick Signalstufen, RSSI in dBm, Verbindungsstatus, RTT und Akku. **A** öffnet und schließt die Diagnoseansicht am Stick.
- **RTT** ist die UDP-Rundlaufzeit zwischen Stick und PC. Der Stick sendet alle zwei Sekunden einen kurzen Ping und meldet den Wert mit der vorhandenen Statusmeldung zurück. Der Wert enthält die Verarbeitungszeit und ist keine Messung der Zeit vom Umschalten am Mixer bis zur Displayanzeige.
- Der Stick meldet **Delayed** ab 750 ms ohne neuen Zustand oder ab 250 ms RTT. Nach etwa 1,5 s ohne neuen Zustand werden beide Ebenen grau. Der PC kann nur aus empfangener Telemetrie schließen und zeigt bei etwa 3,5 s ohne Statusmeldung **Getrennt**.
- Der Tab **Diagnose** unterscheidet WLAN-Ereignisse des Sticks, fehlende PC-Zustände, Pausen im PC-Sendezyklus und Mixerfehler. Die letzten 1.000 Netzwerkereignisse werden lokal in `%LOCALAPPDATA%\TallyPager\Pilot\diagnostics.json` abgelegt und beim nächsten Start wieder eingelesen. Die Datei und der JSON-Export enthalten keine Chat- oder Next-Texte. Die Liste abgeschlossener Chat-Anweisungen gehört zur laufenden Sitzung. Bei vMix auf demselben PC zunächst `127.0.0.1` als Host verwenden. Die Anzeige unterscheidet TCP auf Port **8099**, die Antwort auf `SUBSCRIBE TALLY`, TALLY/XML und eine fehlende Eingangszuteilung.

## Build und Prüfung

Entwicklung erfordert das .NET-10-SDK und PlatformIO. `desktop/build-release.ps1` erstellt die EXE und das separate Firmware-ZIP mit SHA-256-Prüfsummen. Die EXE enthält das Espressif-Flashwerkzeug. Drittanbieterhinweise stehen im Release-Ordner.

`dotnet run --project tests/ProtocolSmoke` prüft UDP-Pairing, Funkbasis-Routing, WLAN-Suche und verschlüsselte Einstellungen, Chat-Quittierungen bei Sitzungswechsel, Profile und Wiederherstellung, Companion HTTP, Simulation, vMix-TCP und Tally-Arbiter-Snapshots. Mit dem Pfad zum Firmware-ZIP als zusätzlichem Argument prüft es auch die Paketstruktur und Prüfsummen. Der [Plan für 0.5](docs/Plan-0.5.md) beschreibt die Feldabnahme und weiteren Integrationen.
