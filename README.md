# TallyPager 0.3.0

Tally und Chat für mobile Kameras mit dem M5StickC Plus2. Die Windows-App verbindet sich direkt mit ATEM und vMix. CameraCue 0.1.4 bleibt als eigener [Build für Tally Arbiter](CameraCue/README.md) verfügbar.

## Start und Einrichtung

1. `TallyPager-Windows-x64.exe` und `TallyPager-Firmware-M5StickC-Plus2-0.3.0.zip` aus `dist/0.3.0/` in denselben Ordner legen. Die EXE per Doppelklick starten.
2. Für jeden Stick auf **Stick einrichten** gehen. USB-Port, IPv4-Adresse des PCs im Kameranetz und Stickname wählen. Bei Bedarf die ZIP auswählen. Enthalten sind die geflashten Images, darunter `firmware.bin`, und ein Manifest. Die App liest das Paket beim Flashen ein und prüft Modell, Version und Prüfsummen.
3. WLAN-Name und Passwort eingeben oder beide Felder für das gespeicherte WLAN leer lassen. Der Stick benötigt ein 2,4-GHz-WLAN und muss den PC per UDP auf Port 46224 erreichen.
4. Unter **Quellen und Zuordnung** ATEM und/oder vMix verbinden. Einen Stick in der Tabelle auswählen, pro Ebene **ATEM**, **vMix** oder **Aus** und den gewünschten Eingang wählen, dann speichern. **Innen und außen tauschen** speichert den Tausch sofort.
5. Unter **Regie** einen oder mehrere Sticks auswählen. Mit **Senden** wird der aktuelle Chat- und Next-Text auf diesen Sticks ersetzt. **Chat und Next leeren** entfernt beide Texte. Chat ist in 0.3.0 eine Regie-Nachricht an die Sticks.

Für die Kaskade kann pro Stick innen sein physischer ATEM-Eingang und außen der in vMix eingespeiste ATEM-Ausgang gewählt werden. Bei direkter vMix-Kamera kann der vMix-Eingang auf jede gewünschte Ebene gelegt werden. Fehlende Quellen oder Zuordnungen zeigen Grau, bestätigter inaktiver Zustand Blau, Preview Grün, Program Rot.

## Simulation und Verbindungsanzeige

- Die Simulation läuft nur für die ausgewählten Sticks. Sie bietet manuelle Zustände, vMix Program ↔ Inaktiv, vMix Program ↔ Quellausfall, wechselndes Program/Preview, ATEM Preview → Program → Inaktiv und eine Unterbrechung des Tally-Versands zum Stick. In den Intervall-Szenarien ist das Intervall die Dauer eines Schritts. Für die Anzeige beider Ausfallstufen am Stick und am PC eignen sich im Unterbrechungsszenario mindestens fünf Sekunden.
- Die simulierte Anzeige ist auf dem Stick mit **TEST** und am PC mit **Simulation** markiert. **Auswahl stoppen** oder **Alle stoppen** stellt die aktuellen Mixerwerte wieder her. Mit der neuen Firmware testen, damit die Markierung auf dem Stick erscheint.
- Der Stick zeigt oben rechts WLAN-Balken und **Verbunden**, **Delayed** oder **Getrennt**. Der PC zeigt pro Stick dieselben Signalstufen, RSSI in dBm, Verbindungsstatus, RTT und Akku. **A** öffnet und schließt die Diagnoseansicht am Stick.
- **RTT** ist die UDP-Rundlaufzeit zwischen Stick und PC. Der Stick sendet alle zwei Sekunden einen kurzen Ping und meldet den Wert mit der vorhandenen Statusmeldung zurück. Der Wert enthält die Verarbeitungszeit und ist keine Messung der Zeit vom Umschalten am Mixer bis zur Displayanzeige.
- Der Stick meldet **Delayed** ab 750 ms ohne neuen Zustand oder ab 250 ms RTT. Nach etwa 1,5 s ohne neuen Zustand werden beide Ebenen grau. Der PC kann nur aus empfangener Telemetrie schließen und zeigt bei etwa 3,5 s ohne Statusmeldung **Getrennt**.

## Build und Prüfung

Entwicklung erfordert das .NET-10-SDK und PlatformIO. `desktop/build-release.ps1` erstellt die EXE und das separate Firmware-ZIP mit SHA-256-Prüfsummen. Die EXE enthält das Espressif-Flashwerkzeug. Drittanbieterhinweise stehen im Release-Ordner.

`dotnet run --project tests/ProtocolSmoke` prüft UDP-Pairing, Telemetrie, Zuordnungen, Simulation und den vMix-TCP-Leser. Mit dem Pfad zum Firmware-ZIP als zusätzlichem Argument prüft es auch die Paketstruktur und Prüfsummen. Der [Entwicklungsplan](docs/Entwicklungsplan.md) beschreibt die Feldabnahme und weiteren Integrationen.
