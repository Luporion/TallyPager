# TallyPager 0.4.0

Tally und Chat für mobile Kameras mit dem M5StickC Plus2. Die Windows-App kann ATEM und vMix direkt lesen oder Tally Arbiter als Quelle verwenden. CameraCue 0.1.4 bleibt als eigener [Build für Tally Arbiter](CameraCue/README.md) verfügbar.

## Start und Einrichtung

1. `TallyPager-Windows-x64.exe` und `TallyPager-Firmware-M5StickC-Plus2-0.4.0.zip` vom [GitHub-Release](https://github.com/Luporion/TallyPager/releases/tag/v0.4.0) in denselben Ordner legen. Die EXE per Doppelklick starten. Lokale Builds liegen in `dist/0.4.0/`.
2. Für jeden Stick auf **Stick einrichten** gehen. USB-Port, IPv4-Adresse des PCs im Kameranetz und Stickname wählen. Bei Bedarf die ZIP auswählen. Enthalten sind die geflashten Images, darunter `firmware.bin`, und ein Manifest. Die App liest das Paket beim Flashen ein und prüft Modell, Version und Prüfsummen.
3. WLAN-Name und Passwort eingeben oder beide Felder für das gespeicherte WLAN leer lassen. Der Stick benötigt ein 2,4-GHz-WLAN und muss den PC per UDP auf Port 46224 erreichen.
4. Unter **Quellen und Zuordnung** die gewünschten Quellen verbinden. Einen Stick in der Tabelle auswählen, pro Ebene Quelle und Eingang wählen und die Zuordnung speichern. **Aus · ausblenden** entfernt Farbe und Quellzeile für diese Ebene. **Innen und außen tauschen** speichert den Tausch sofort. Der Stick zeigt die zugeordnete Quelle an, zum Beispiel `ATEM: LIVE`.
5. Unter **Regie** einen oder mehrere Sticks auswählen. **Enter** sendet den Inhalt der gerade bearbeiteten Zeile. Jede Zeile hat **Leeren** und **Senden**. **Chat und Next senden** überträgt beide Felder. Einzelaktionen erhalten den jeweils anderen Text pro Stick.

Für die Kaskade kann pro Stick innen sein physischer ATEM-Eingang und außen der in vMix eingespeiste ATEM-Ausgang gewählt werden. Bei direkter vMix-Kamera kann der vMix-Eingang auf jede gewünschte Ebene gelegt werden. Fehlende Quellen oder Zuordnungen zeigen Grau, bestätigter inaktiver Zustand Blau, Preview Grün, Program Rot.

### Tally Arbiter

Unter **Quellen und Zuordnung → Tally Arbiter** den Host, `Host:Port` oder die HTTP-/HTTPS-Adresse des laufenden Servers eingeben und verbinden. Ein reiner Host verwendet Port 4455. Danach für die Ebene **Tally Arbiter · Gerät**, **Tally Arbiter · ATEM** oder **Tally Arbiter · vMix** wählen und das gewünschte Gerät aus dessen Liste übernehmen. **Gerät** wertet die aktiven Beiträge aller erreichbaren, zugeordneten Quellen aus. **ATEM** und **vMix** filtern auf den jeweiligen Mischertyp. Programm hat Vorrang vor Preview. Die App liest Tally und Gerätezuordnungen über Socket.IO. Chat und Next kommen weiterhin aus der Windows-App.

## Simulation und Verbindungsanzeige

- Die Simulation läuft nur für die ausgewählten Sticks. Sie bietet manuelle Zustände, vMix Program ↔ Inaktiv, vMix Program ↔ Quellausfall, wechselndes Program/Preview, ATEM Preview → Program → Inaktiv und eine Unterbrechung des Tally-Versands zum Stick. In den Intervall-Szenarien ist das Intervall die Dauer eines Schritts. Für die Anzeige beider Ausfallstufen am Stick und am PC eignen sich im Unterbrechungsszenario mindestens fünf Sekunden.
- Die simulierte Anzeige ist auf dem Stick mit **TEST** und am PC mit **Simulation** markiert. **Auswahl stoppen** oder **Alle stoppen** stellt die aktuellen Mixerwerte wieder her. Mit der neuen Firmware testen, damit die Markierung auf dem Stick erscheint.
- Der Stick zeigt oben rechts WLAN-Balken und RTT. **Verbunden** erscheint nach dem Aufbau der Verbindung für etwa zwei Sekunden. **Delayed** und **Getrennt** bleiben sichtbar, solange der Zustand anhält. Der PC zeigt pro Stick Signalstufen, RSSI in dBm, Verbindungsstatus, RTT und Akku. **A** öffnet und schließt die Diagnoseansicht am Stick.
- **RTT** ist die UDP-Rundlaufzeit zwischen Stick und PC. Der Stick sendet alle zwei Sekunden einen kurzen Ping und meldet den Wert mit der vorhandenen Statusmeldung zurück. Der Wert enthält die Verarbeitungszeit und ist keine Messung der Zeit vom Umschalten am Mixer bis zur Displayanzeige.
- Der Stick meldet **Delayed** ab 750 ms ohne neuen Zustand oder ab 250 ms RTT. Nach etwa 1,5 s ohne neuen Zustand werden beide Ebenen grau. Der PC kann nur aus empfangener Telemetrie schließen und zeigt bei etwa 3,5 s ohne Statusmeldung **Getrennt**.

## Build und Prüfung

Entwicklung erfordert das .NET-10-SDK und PlatformIO. `desktop/build-release.ps1` erstellt die EXE und das separate Firmware-ZIP mit SHA-256-Prüfsummen. Die EXE enthält das Espressif-Flashwerkzeug. Drittanbieterhinweise stehen im Release-Ordner.

`dotnet run --project tests/ProtocolSmoke` prüft UDP-Pairing, Telemetrie, Zuordnungen, getrennten Textversand, Simulation, den vMix-TCP-Leser und Tally-Arbiter-Snapshots. Mit dem Pfad zum Firmware-ZIP als zusätzlichem Argument prüft es auch die Paketstruktur und Prüfsummen. Der [Entwicklungsplan](docs/Entwicklungsplan.md) beschreibt die Feldabnahme und weiteren Integrationen.
