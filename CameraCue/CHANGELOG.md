# Changelog

## 0.1.4 - 2026-09-23

### Verbindungsstabilitaet / Recovery

- WiFi Power Save wird jetzt doppelt explizit deaktiviert (`WiFi.setSleep(false)` + `WIFI_PS_NONE`).
- CPU-Takt von 80 auf 160 MHz angehoben, damit WLAN, Socket.IO, Web-Settings und Display mehr Reserve haben.
- Socket.IO wird nur noch verarbeitet, solange WLAN wirklich verbunden ist.
- Neuer TA-Reconnect-Watchdog: bleibt WLAN online, aber Socket.IO offline, wird die Verbindung nach 8 Sekunden sauber neu aufgebaut.
- Nach WLAN-Wiederkehr wird Socket.IO komplett neu gestartet und der Listener neu registriert.
- `device_states` wird zusammen mit den Metadaten periodisch erneut angefordert, damit verpasste Zustandsupdates automatisch geheilt werden.
- Engine.IO-v3-Pfad ist fuer Tally Arbiter 3.3.0 explizit festgelegt (`/socket.io/?EIO=3`).
- Bei fehlendem WLAN wird kein Socket.IO-Verbindungsversuch mehr gestartet; stattdessen bleibt der Tally-Zustand UNKNOWN.
- Hauptanzeige unterscheidet jetzt sichtbar `WIFI OFF` und `TA OFFLINE`.
- Diagnose trennt WiFi-Reconnects, TA-Reconnects und echte Socket-Disconnects.

### Einstellungen / WLAN

- Bereits konfigurierte Geraete warten beim Booten nicht mehr minutenlang im WiFiManager-Portal: gespeichertes WLAN wird kurz getestet, danach startet automatisch der nicht-destruktive CameraCue-Setup-AP.
- Die gespeicherte SSID wird im Webinterface auch dann angezeigt, wenn der Stick gerade offline ist.
- Leeres Passwortfeld behaelt jetzt wirklich das bisherige Passwort – auch beim Wechsel der SSID.
- Fuer offene WLANs gibt es eine eigene Checkbox.
- WLAN-Rollback nutzt auch im Offline-Fall die gespeicherten alten Zugangsdaten.

### Tally Arbiter / Meldungen

- Tally-Arbiter-Systemmeldungen werden primaer ueber den offiziellen Nachrichtentyp `server` erkannt und nicht mehr als Regie-Cue angezeigt.
- Textbasierter Filter bleibt als Fallback fuer aeltere Server erhalten.
- `setHoldThresh()` wird nicht mehr aufgerufen; die A+B-Haltezeiten werden ausschliesslich mit `millis()` gemessen. Damit wird der fruehere Button-Startpfad komplett vermieden.

## 0.1.3 - 2026-09-23

### Einstellungen ohne Reset

- Neue lokale Settings-Weboberflaeche auf Port 80. Bei bestehender WLAN-Verbindung einfach `http://<IP-des-M5Stick>/` oeffnen.
- WLAN-SSID/Passwort, Tally-Arbiter-Host/Port, ATEM-/vMix-Source-Hints, Helligkeit und Buzzer koennen dort geaendert werden.
- **A+B fuer 3 Sekunden** startet einen nicht-destruktiven Setup-AP `CameraCue-XXXXXX-Setup` (Passwort `cameracue`). Bestehende Einstellungen werden nicht geloescht. Weboberflaeche: `http://192.168.4.1/`.
- **A+B fuer 10 Sekunden** oeffnet nur noch eine Factory-Reset-Bestaetigung. Danach A = Ja, B = Nein.
- Sicherer WLAN-Wechsel: neue Credentials werden nach Neustart getestet; bei Fehlschlag versucht CameraCue automatisch die vorherigen Zugangsdaten wiederherzustellen.
- USB/Serial-CLI bei 115200 Baud: `status`, `config`, `setup`, `reboot`, `set ta <host> <port>`, `set wifi <ssid> <password>`.

### Stabilitaet / Diagnose

- WiFi Sleep bleibt explizit deaktiviert.
- WLAN-Verlust setzt beide Tally-Ebenen sofort auf UNKNOWN statt SAFE.
- Automatische WLAN-Reconnect-Versuche; nach wiederhergestelltem WLAN wird die Socket.IO-/Tally-Arbiter-Verbindung komplett neu aufgebaut.
- Diagnoseansicht zeigt IP, Settings-URL, RSSI, Reconnect-Zaehler und letzte Disconnect-Ursache.
- `/status` liefert kompakte JSON-Diagnosedaten.
- Bekannte Tally-Arbiter-Systemmeldungen wie Listener-Connect/Disconnect und Test-Mode werden nicht mehr als grosse Regieanweisung angezeigt.
- Optionaler kurzer Buzzer bei neuen echten Regieanweisungen.

## 0.1.2

- Fixes the boot-loop / `StoreProhibited` crash seen on M5StickC Plus2 immediately after startup.
- Uses the native `M5.BtnA` / `M5.BtnB` objects instead of the `StickCP2.BtnA` / `StickCP2.BtnB` wrapper references.
- Uses `M5.update()` for the button state machine.
- Enables PlatformIO `esp32_exception_decoder` for readable crash traces in the serial monitor.

## 0.1.1 - 2026-09-22

- Added the missing local `boards/m5stick-cplus2.json` definition.
- Pinned PlatformIO `espressif32` to 6.9.0 for reproducible Arduino-ESP32 2.0.17 builds.
- Ignored the unused `DFRobot_GP8XXX` dependency pulled in by M5StickCPlus2.
- Updated `build.cmd`, `upload.cmd`, and `monitor.cmd` to automatically use PlatformIO's VS Code virtual-environment executable when `pio` is not on PATH.

## 0.1.0 - 2026-09-22

- Initial CameraCue firmware prototype for M5StickC Plus2.
- Tally Arbiter Socket.IO listener.
- Separate ATEM inner tally and vMix outer tally display.
- Tally Arbiter message/CUE and NEXT parsing.
- WiFiManager setup, OTA, diagnostics, brightness control and safe unknown state.
