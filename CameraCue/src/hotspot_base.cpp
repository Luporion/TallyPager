#include <Arduino.h>
#include <ArduinoJson.h>
#include <M5StickCPlus2.h>
#include <Preferences.h>
#include <WiFi.h>
#include "hotspot_link.h"
#include <esp_system.h>

namespace {
using namespace hotspot;
Link traffic;
String deviceId, ssid, password;
int channel = 6;
bool ready = false;
char line[envelopeSize + packetLimit + 1];
size_t used = 0;
bool overflow = false;
uint32_t restartAt = 0;

bool loadSettings() {
  Preferences prefs;
  if (!prefs.begin("tallyhotspot", false)) return false;
  if (prefs.isKey("channel")) {
    int stored = prefs.getUChar("channel", 6);
    if (validChannel(stored)) channel = stored;
  }
  if (prefs.isKey("password")) password = prefs.getString("password", "");
  bool saved = password.length() == 16;
  if (!saved) {
    // Enable the Wi-Fi hardware entropy source before generating the WPA2 password.
    if (WiFi.mode(WIFI_STA)) {
      uint32_t random[2];
      esp_fill_random(random, sizeof(random));
      char secret[17];
      snprintf(secret, sizeof(secret), "%08lx%08lx", static_cast<unsigned long>(random[0]),
          static_cast<unsigned long>(random[1]));
      password = secret;
      saved = prefs.putString("password", password) == password.length();
    }
    if (!saved) password = "";
  }
  prefs.end();
  return saved;
}

void status() {
  JsonDocument doc;
  doc["version"] = TALLY_PAGER_VERSION;
  doc["id"] = deviceId;
  doc["board"] = "m5stick-cplus2";
  doc["configured"] = ready;
  doc["baseRole"] = true;
  doc["mode"] = "hotspot";
  doc["radioReady"] = ready;
  doc["ip"] = gateway.toString();
  doc["channel"] = channel;
  doc["hotspotSsid"] = ssid;
  doc["hotspotPassword"] = password;
  doc["features"].to<JsonArray>().add("hotspot");
  Serial.print("TP ");
  serializeJson(doc, Serial);
  Serial.println();
}

void command() {
  line[used] = '\0';
  if (strcmp(line, "TP STATUS") == 0) { status(); return; }
  if (strncmp(line, "TP AP ", 6) == 0) {
    const char* requested = line + 6;
    int value = atoi(requested);
    if (!validChannel(value) || String(value) != requested) { Serial.println("TP ERROR channel"); return; }
    Preferences prefs;
    bool saved = prefs.begin("tallyhotspot", false);
    if (saved) {
      saved = prefs.putUChar("channel", value) == sizeof(uint8_t);
      prefs.end();
    }
    if (!saved) { Serial.println("TP ERROR storage"); return; }
    Serial.println("TP OK");
    restartAt = millis() + 350;
    return;
  }
  if (ready) traffic.fromSerial(line, used, linkPrefix);
}

void readSerial() {
  for (int i = 0; i < 2048 && Serial.available(); ++i) {
    char ch = Serial.read();
    if (ch == '\r') continue;
    if (ch == '\n') {
      if (used && !overflow) command();
      used = 0;
      overflow = false;
    } else if (ch < ' ' || ch > '~' || used == sizeof(line) - 1) overflow = true;
    else if (!overflow) line[used++] = ch;
  }
}

void draw(int clients) {
  auto& display = StickCP2.Display;
  display.fillScreen(TFT_BLACK);
  display.setCursor(5, 8);
  display.setTextSize(2);
  display.setTextColor(TFT_WHITE);
  display.println("WLAN-Bruecke");
  display.setTextSize(1);
  display.setCursor(5, 41);
  display.println(ssid);
  display.setCursor(5, 60);
  display.println("Basis-Server: " + gateway.toString());
  display.setCursor(5, 78);
  display.println("Kanal: " + String(channel));
  display.setCursor(5, 103);
  display.println(ready ? "WLAN-Geraete: " + String(clients) + " von " + String(maxClients) : String("WLAN konnte nicht starten"));
}
}

void setup() {
  Serial.setRxBufferSize(4096);
  Serial.begin(460800);
  StickCP2.begin(M5.config());
  StickCP2.Display.setRotation(1);
  StickCP2.Display.setBrightness(110);
  char id[13];
  snprintf(id, sizeof(id), "%012llx", static_cast<unsigned long long>(ESP.getEfuseMac()));
  deviceId = id;
  ssid = "TallyPager-" + deviceId.substring(6);
  WiFi.persistent(false);
  WiFi.setAutoReconnect(false);
  ready = loadSettings() && WiFi.mode(WIFI_AP) && WiFi.softAPConfig(gateway, gateway, mask) &&
      WiFi.softAP(ssid.c_str(), password.c_str(), channel, 0, maxClients) && traffic.begin();
  if (!ready) WiFi.mode(WIFI_OFF);
}

void loop() {
  readSerial();
  if (ready) traffic.toSerial(linkPrefix);
  static int previousClients = -1;
  static uint32_t checkedAt = 0;
  uint32_t now = millis();
  if (previousClients < 0 || now - checkedAt >= 500) {
    checkedAt = now;
    int clients = ready ? WiFi.softAPgetStationNum() : 0;
    if (clients != previousClients) draw(clients);
    previousClients = clients;
  }
  if (restartAt && static_cast<int32_t>(now - restartAt) >= 0) ESP.restart();
  delay(1);
}
