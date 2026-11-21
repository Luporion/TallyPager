#include <Arduino.h>
#include <ArduinoJson.h>
#include <M5StickCPlus2.h>
#include <Preferences.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <esp_system.h>

namespace {
constexpr uint16_t tallyPort = 46224;
constexpr size_t packetLimit = 900;
constexpr size_t envelopeSize = 21;
constexpr int maxClients = 4;
const IPAddress gateway(192, 168, 4, 1), mask(255, 255, 255, 0);
WiFiUDP traffic;
String deviceId, ssid, password;
int channel = 6;
bool ready = false;
char line[envelopeSize + packetLimit + 1];
size_t used = 0;
bool overflow = false;
uint32_t restartAt = 0;

bool validChannel(int value) { return value == 1 || value == 6 || value == 11; }

bool clientAddress(const IPAddress& ip) {
  return ip[0] == gateway[0] && ip[1] == gateway[1] && ip[2] == gateway[2] && ip[3] >= 2 && ip[3] <= 254;
}

bool tallyPayload(const char* bytes, size_t length) {
  if (length < 4 || length > packetLimit || memcmp(bytes, "TP1|", 4) != 0) return false;
  for (size_t i = 4; i < length; ++i)
    if (bytes[i] < ' ' || bytes[i] > '~') return false;
  return true;
}

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
  if (!ready || used <= envelopeSize || strncmp(line, "TP LINK ", 8) != 0 || line[20] != ' ' ||
      !tallyPayload(line + envelopeSize, used - envelopeSize)) return;
  uint8_t bytes[6]{};
  for (size_t i = 0; i < 12; ++i) {
    char ch = line[8 + i];
    int digit = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : -1;
    if (digit < 0) return;
    bytes[i / 2] = (bytes[i / 2] << 4) | digit;
  }
  const IPAddress ip(bytes[0], bytes[1], bytes[2], bytes[3]);
  const uint16_t port = (bytes[4] << 8) | bytes[5];
  if (clientAddress(ip) && port && traffic.beginPacket(ip, port)) {
    traffic.write(reinterpret_cast<const uint8_t*>(line + envelopeSize), used - envelopeSize);
    traffic.endPacket();
  }
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

void readNetwork() {
  char packet[packetLimit];
  for (int i = 0; i < 4; ++i) {
    int size = traffic.parsePacket();
    if (size <= 0) return;
    IPAddress ip = traffic.remoteIP();
    uint16_t port = traffic.remotePort();
    if (size > static_cast<int>(sizeof(packet)) || !clientAddress(ip) || !port) { traffic.flush(); continue; }
    if (traffic.read(reinterpret_cast<uint8_t*>(packet), size) != size || !tallyPayload(packet, size)) continue;
    char header[envelopeSize + 1];
    snprintf(header, sizeof(header), "TP LINK %02x%02x%02x%02x%04x ", ip[0], ip[1], ip[2], ip[3], port);
    Serial.write(reinterpret_cast<const uint8_t*>(header), envelopeSize);
    Serial.write(reinterpret_cast<const uint8_t*>(packet), size);
    Serial.write('\n');
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
      WiFi.softAP(ssid.c_str(), password.c_str(), channel, 0, maxClients) && traffic.begin(gateway, tallyPort);
  if (!ready) WiFi.mode(WIFI_OFF);
}

void loop() {
  readSerial();
  if (ready) readNetwork();
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
