# TallyPager 0.5 · Regieprofile, Schnelltexte und Companion

Planungsstand 15. November 2026

## Ziel und Priorität

Die Regie soll einen Aufbau auswählen, am Ersatzrechner aus einer Sicherung wiederherstellen und kurze Anweisungen aus der App oder vom Stream Deck an die richtige Kamera senden können. Bei wichtigen Anweisungen soll der Kameraoperator mit **B** quittieren. Die direkte Anbindung an ATEM und vMix sowie Tally Arbiter als optionale Quelle bleiben nutzbar. Die erste Integration von Middle Control für Windows läuft über Bitfocus Companion.

Die Freigabe von 0.5.0 setzt voraus, dass wir die Probleme aus dem heutigen Dienst auf dem tatsächlichen Regie-PC untersucht und den Aufbau im Gemeinde-WLAN mit mehreren Sticks geprobt haben. Funktionen und Fehlerdiagnose können parallel entwickelt werden.

## 1. Heutiger Praxistest und Diagnose

Gemeldet sind zeitweilige Verbindungsunterbrechungen bei einem Stick neben der Regie und eine fehlgeschlagene vMix-Anbindung. Die Ursache ist offen. 0.4.0 trennt auf dem Stick die WLAN-Verbindung vom Empfang der PC-Zustände. Bei 750 ms ohne Zustand erscheint **Delayed**, bei etwa 1,5 s verfällt der Tally-Zustand. Hohe RTT kann ebenfalls **Delayed** auslösen. Der vMix-Adapter verbindet TCP und fordert unter anderem `SUBSCRIBE TALLY`, `TALLY` und `XML` an. Seine Fehleranzeige fasst momentan mehrere Verbindungs- und Protokollfehler zusammen.

| Prüfung | Vorgehen und erwartete Aussage |
|---|---|
| vMix-Rechner und Port | Läuft vMix auf demselben PC wie TallyPager, mit `127.0.0.1` prüfen. Läuft es auf einem anderen Rechner, dessen LAN-IP verwenden. Auf dem TallyPager-PC `Test-NetConnection -ComputerName <vMix-Rechner> -Port 8099` ausführen. Ein erfolgreicher TCP-Test zeigt die Erreichbarkeit des Ports. Auf dem vMix-PC zusätzlich prüfen, welcher Prozess auf 8099 lauscht. Im früheren Aufbau konnte der vMix-Emulator von Tally Arbiter dort statt vMix antworten. Bei fehlendem Zugriff die konkrete Windows-Firewallregel und die LAN/API-Einstellungen in vMix prüfen. |
| vMix-Protokoll und Zuordnung | Eine eingebaute Diagnose soll getrennt melden, ob TCP erreichbar ist, ob `SUBSCRIBE TALLY`, eine gültige Tally-Antwort und die XML-Eingangsliste ankommen und ob der zugeordnete Eingang gefunden wurde. Programmeingang der mobilen Kamera bzw. des ATEM-Feeds mit einer echten Umschaltung prüfen. Beim Laden oder Umsortieren eines vMix-Presets den Input-Key gegen den neu geladenen Eingangskatalog prüfen. Bei fehlendem Key Grau und eine klare Meldung anzeigen. |
| Funkstrecke | Auf dem Stick WLAN-Verlust, verlorene IP und Wiederverbindung mit Zeit und WLAN-Reason-Code erfassen. Bei bestehendem WLAN zusätzlich Alter des letzten gültigen Snapshots, Sequenzlücken, RTT und Dauer bis zur Wiederherstellung zählen. Auf dem PC eine Zeitachse für Quellenstatus, letzte Telemetrie und ungewöhnlich lange Lücken im Sendezyklus anzeigen. Damit lassen sich Funkunterbrechung, Warteschlange am PC und Ausfall eines Mixers auseinanderhalten. |
| Wiederholbarer Feldtest | Einen Stick neben der Regie und einen auf dem Laufweg der mobilen Kamera beobachten, danach mit drei Sticks testen. Jeweils vMix und ATEM einzeln trennen, den PC neu starten und WLAN kurz unterbrechen. Dauer, betroffene Ebene und Wiederherstellung erfassen. RTT und die Zeit von der tatsächlichen Mixerumschaltung bis zur Anzeige getrennt messen. Vorhandene Grau-Schwellen zunächst beibehalten, bis die Ursache belegt ist. |

Die Diagnose soll mit einem kleinen rollierenden Ereignispuffer arbeiten. Der Export enthält Zeit, Gerätealias, Quelle, Fehlerklasse und Laufzeiten. Zugangsdaten und Nachrichtentexte bleiben außerhalb des Diagnoseexports. Im UI-Timer von 0.4.0 wird auch der UDP-Core bedient. Bei gemessenen Sendeaussetzern unter UI-Last den Netzwerk-Takt in einen eigenen asynchronen Ablauf verlagern.

- [vMix TCP API und TALLY](https://www.vmix.com/help28/TCPAPI.html)
- [vMix LAN- und API-Einstellungen](https://sdapi00.vmix.com/help29/WebController.html)
- [vMix Input-Keys](https://www.vmix.com/help28/DeveloperAPI.html)
- [WLAN-Ereignisse und Disconnect-Gründe beim ESP32](https://docs.espressif.com/projects/arduino-esp32/en/latest/api/wifi.html)

## 2. Regieprofile und Sicherung

### Anleitung aus Sicht der Regie

1. Den aktuellen Aufbau als **Standard** übernehmen. Weitere Profile wie **Gottesdienst**, **Taufe** oder **Test** durch Kopieren anlegen.
2. Pro Profil Mixeradressen, verwendete Quellen, Kamera-Rollen, Innen-/Außenbelegung, ausgeblendete Ebenen, Zielgruppen und Schnelltexte speichern. Optional gehört die Middle-Control-Camera-ID zur Kamera-Rolle. Bei vMix den Input-Key, bei Tally Arbiter die Geräte-ID und beim ATEM die Eingang-ID speichern.
3. Ein Profilwechsel zeigt zuerst Quellen und Änderungen. Erst **Aktivieren** übernimmt ihn. Die Ansicht zeigt während des Neuaufbaus Grau für noch ungeprüfte Quellen. Der Wechsel beendet Simulationen und aktuelle Chats/Next-Texte des alten Aufbaus. Begonnene Quittierungen werden als abgebrochen abgeschlossen und bleiben im Sitzungsverlauf nachvollziehbar.
4. Mit **Profil exportieren** ein Setup weitergeben. Mit **Sicherung exportieren** alle Profile, Gruppen und Schnelltexte in einer Datei sichern. Beim Import zuerst Vorschau, fehlende Quellen und ungebundene Kamera-Rollen anzeigen. **Als Kopie importieren** lässt den bisherigen Aufbau nutzbar. **Ersetzen** sichert vorher automatisch den vorhandenen Stand. Ein Validierungsfehler lässt ihn unangetastet.

### Daten und Wiederherstellung

- Ein versioniertes UTF-8-JSON-Format mit JSON Schema 2020-12 verwenden. IDs für Profile, Rollen und Vorlagen bleiben beim Umbenennen und bei der Wiederherstellung stabil. Eine zusätzliche Kopie bekommt eigene IDs und aktualisierte interne Verweise. Bestehende Stream-Deck-Tasten behalten ihre bisherige Vorlage. Der Import prüft Schema, Größen und Referenzen, bevor Dateien atomar ersetzt werden. Eine unbekannte neuere Formatversion wird mit konkreter Versionsmeldung abgelehnt.
- Physischer Stick und Kamera-Rolle bleiben getrennt. Die lokale Bindung sagt, welcher gepaarte Stick heute „Mobile Kamera“ ist. Auf dem Ersatz-PC kann dieselbe Rolle einem anderen Stick zugeordnet werden. Profile enthalten keine WLAN-Passwörter, Geräteschlüssel, API-Tokens oder noch offenen Regieanweisungen.
- Bestehendes `devices.json` und die gespeicherte Tally-Arbiter-Adresse einmalig in ein Standardprofil migrieren. Die Migration muss wiederholbar sein und vorhandene Geräteverbindungen auf demselben PC erhalten. Geräteschlüssel separat im Windows-Benutzerkontext sichern, beispielsweise mit DPAPI. Ein Profilimport auf einem Ersatz-PC stellt Zuordnungen und Schnelltexte wieder her. Dort werden die Sticks per USB gepaart. Bei geänderter PC-Adresse wird diese beim Pairing ebenfalls geschrieben.
- Eine rechnerübergreifende verschlüsselte Notfallsicherung mit Geräteschlüsseln ist eine eigene optionale Ausbaustufe. Den Bedarf zuerst messen, indem wir den Restore auf einem sauberen Windows-PC mit drei Sticks durchführen. DPAPI eignet sich für lokale Schlüsselablage. Eine portable Notfallsicherung braucht ein davon unabhängiges Wiederherstellungsverfahren.

Das Vorbild für Vorschau und gezielten Import ist Companion mit seiner `.companionconfig`. QLab zeigt Vorlagen und automatische Sicherungen als zwei unterschiedliche Bedienvorgänge. JSON Schema liefert das allgemeine Validierungsformat. Für die spezifischen TallyPager-Zuordnungen wurde in den untersuchten Systemen kein gemeinsames Austauschformat gefunden.

- [Companion Import und Export](https://companion.free/user-guide/v4.1/config/import-export/)
- [QLab Workspace-Sicherungen und Vorlagen](https://reference.qlab.app/docs/v5/fundamentals/workspace-settings/)
- [JSON Schema 2020-12](https://json-schema.org/draft/2020-12)
- [Microsoft ProtectedData und DPAPI](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)

## 3. Schnelltexte und Quittierung

Die Regie bekommt eine kleine Leiste mit Favoriten. Eine Vorlage hat Namen, Chat- und/oder Next-Text, Zielrolle oder Zielgruppe und optional **Quittierung anfordern**. Startbeispiele sind „Bitte weiter öffnen“, „Position halten“ und „Bereit für Taufe?“. Vorlagen lassen sich direkt senden oder vor dem Senden in die bestehenden Eingabefelder übernehmen. Beim Versand wird die Zielgruppe einmal auf die aktuell gebundenen Sticks aufgelöst. Eine ungebundene Kamera wird gemeldet. Chat und Next behalten ihre getrennten Sende- und Löschfunktionen.

| Anzeige in der Regie | Auslöser |
|---|---|
| **Ausstehend** | Die Anweisung hat eine Nachrichten-ID, aber der PC hat noch keinen aktuellen Zustellweg zum Stick. |
| **Gesendet** | Der PC hat einen Snapshot mit der Nachrichten-ID an den Stick übertragen. |
| **Empfangen** | Der Stick hat die konkrete Nachrichten-ID als aktiven Chat bestätigt. Die allgemeine Snapshot-Bestätigung bleibt separat in der Verbindungsdiagnose. |
| **Quittiert** | Der Kameraoperator drückt **B** im Hauptbild, während genau diese quittierpflichtige Nachricht aktiv angezeigt wird. **A** bleibt Diagnose. |
| **Abgelaufen / ersetzt** | Die Frist endet, der Text wird gelöscht oder eine neue Chat-Nachricht übernimmt den Platz. Die bisherige Anweisung wird geschlossen. |

- Der erste Umfang unterstützt Quittierung für **Chat**. Next bleibt der Hinweis auf den nächsten Programmpunkt. Pro Stick gibt es einen aktiven Chat. Ein erneutes Senden desselben Texts erzeugt eine neue ID und verlangt bei Bedarf eine neue Quittierung. Für quittierpflichtige Vorlagen zunächst 120 Sekunden Gültigkeit erproben. Ob normale Schnelltexte auslaufen, lässt sich pro Vorlage bestimmen.
- Die Nachricht bleibt im aktuellen authentifizierten UDP-Snapshot, solange sie gültig ist. Empfang und Quittierung werden anhand von Geräte-ID und Nachrichten-ID verarbeitet. Der Stick wiederholt eine Quittierung bis zur Rückbestätigung durch den PC, solange die Nachricht gültig ist. Wiederholte und verspätete Quittierungen dürfen weder doppelt zählen noch eine neuere Anweisung bestätigen. Nach einem kurzen Aussetzer übernimmt der Stick die noch gültige Anweisung aus dem aktuellen Snapshot. Nach Neustart der PC-App beginnt eine neue Regiesitzung.
- Geräte mit 0.4-Firmware erhalten weiter normalen Chat und Next. Eine Vorlage mit verlangter Quittierung meldet für diese Geräte **Firmware-Update nötig**. Der PC muss die Fähigkeit pro Stick erkennen. Für den neuen Zustand kommen eine gezielte Empfangsmeldung und eine explizite Quittierung zum bestehenden Protokoll hinzu.

XMPP liefert mit XEP-0184 und XEP-0333 erprobte Begriffe für Empfang und Anzeige. Die menschliche Bestätigung ist bei TallyPager ein eigener Schritt mit einer physischen Taste. Das Muster lässt sich auf den bestehenden Transport übertragen.

- [XEP-0184 Message Delivery Receipts](https://xmpp.org/extensions/xep-0184.html)
- [XEP-0333 Displayed Markers](https://xmpp.org/extensions/xep-0333.html)
- [QLab Workspace und Cue Carts](https://qlab.app/docs/v5/fundamentals/workspace/)

## 4. Bitfocus Companion und Middle Control

### 0.5.0 · nutzbar mit vorhandenen Companion-Modulen

Die Windows-App erhält eine kleine versionierte HTTP/JSON-Schnittstelle. **Generic HTTP** in Companion kann mit `POST` Schnelltexte auslösen oder Felder leeren. Zunächst läuft die Schnittstelle lokal auf dem Regie-PC und verwendet ein separates API-Token. Lesezugriffe liefern Gerätestatus und Nachrichtenstatus. Schreibzugriffe laufen durch dieselbe Validierung und Ziellogik wie die Windows-Oberfläche. Keine direkte Umschaltung der Tally-Farben über diese Schnittstelle. Die API wird mit OpenAPI beschrieben. Eine optionale Anforderungs-ID ermöglicht das sichere Wiederholen eines Aufrufs nach einem Verbindungsfehler.

Beispiel für eure Regie. Eine Stream-Deck-Taste ruft mit dem vorhandenen **Middle Things Middle Control**-Modul ein Preset für eine feste Camera ID auf und sendet anschließend über **Generic HTTP** den TallyPager-Schnelltext „Bitte Position bestätigen“ an die Rolle **Mobile Kamera**. Companion löst beide Aktionen aus. Die Rückmeldung zur Kamerabewegung kommt aus Middle Control und die menschliche Quittierung vom Pager. Die Nachricht behauptet keinen erfolgreichen Preset-Abschluss allein aufgrund des Tastendrucks.

Für **Middle Control 4.2.0 Beta 6 auf Windows** ist der dokumentierte Companion-Weg TCP **11580**. Mit geeigneter Modulversion unterstützt **Monitor all cameras** Daten je Camera ID wie Aufnahme, Preset oder Akku. Die separate External API auf **11584** ist laut Middle Things für macOS. ATEM, vMix oder Tally Arbiter liefern weiterhin das Tally. Für die Windows-Ausbaustufe nutzt TallyPager die dokumentierte Companion-Integration und lässt das Middle-Control-Modul die Kamerasteuerung übernehmen.

### 0.5.x · eigenes Companion-Modul

Ein kleines Modul nach dem Bitfocus-TypeScript-Template ersetzt manuell konfigurierte HTTP-Aktionen durch auswählbare Schnelltexte, Rollen und Gruppen. Startumfang sind Schnelltext senden, Chat/Next gezielt leeren und Feedbacks für Verbunden/Delayed/Getrennt, beide Tally-Ebenen sowie **Quittierung offen / erhalten**. Die IDs von Vorlagen und Rollen bleiben beim Umbenennen stabil, damit Stream-Deck-Aktionen weiterhin funktionieren. Test zuerst als lokales Companion-Modul. Katalogveröffentlichung folgt getrennt nach der Abnahme. Das vorhandene Tally-Arbiter-Modul ist ein Referenzprojekt für Tally-Feedback und kann parallel verwendet werden.

- [Generic HTTP Requests in Companion](https://github.com/bitfocus/companion-module-generic-http/blob/master/companion/HELP.md)
- [Bitfocus-Methode für eigene Module und Aktionen](https://companion.free/for-developers/module-development/module-development-101/)
- [Tally Arbiter und Companion](https://docs.tallyarbiter.com/docs/intro/)
- [OpenAPI 3.1.2](https://spec.openapis.org/oas/v3.1.2.html)
- [Middle Control mit Companion und Camera IDs](https://www.middlethings.co/support/docs/joysticks-and-controllers/bitfocus-companion/)
- [Middle Control Downloads und Windows-Version](https://www.middlethings.co/support/docs/downloads/)
- [Plattformen und Ports der Middle-Control-API](https://www.middlethings.co/support/docs/joysticks-and-controllers/external-sdk-api/)

## Lieferfolge und Abnahme

1. **Diagnose und Reproduktion.** Verbindungsprüfung im tatsächlichen vMix-Aufbau. Verbindungsfehler und fehlende Eingangszuordnung in getrennten Tests nachstellen. Funkdaten am Platz der Regie und entlang des Laufwegs aufnehmen. Die bisherige Zielgröße von p95 ≤ 250 ms vom Mixervorgang bis zur sichtbaren Anzeige im Normalbetrieb prüfen und die RTT separat erfassen.
2. **Profile und Restore.** 0.4-Migration testen. Zwei Aufbauten wechseln. Defekte Sicherung und ID-Konflikte prüfen. Auf einem zweiten Rechner einrichten und dabei die Zeit samt USB-Pairing messen.
3. **Schnelltexte und Quittierung.** Einzelgerät und Gruppe testen. Dieselbe Nachricht zweimal senden, eine Nachricht ersetzen, während einer Störung quittieren und Wiederverbindung prüfen. Tally muss unabhängig von Chat und Quittierung weiterlaufen.
4. **Companion und Middle Control.** Schnelltext über Generic HTTP auslösen. Eine kombinierte Stream-Deck-Taste mit einer festen Camera ID und Rolle „Mobile Kamera“ prüfen. Offline-Stick, falsches Token und Ausfall von Middle Control getrennt testen.
5. **Feldfreigabe.** Windows-EXE und Firmware-ZIP bleiben getrennt. Mit drei physischen Sticks und realen Mixern im Gemeinde-WLAN proben. Native Companion-Bedienung und ein optionaler erweiterter Restore folgen als 0.5.x.
