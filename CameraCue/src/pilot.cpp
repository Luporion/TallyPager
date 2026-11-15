#include <Arduino.h>
#include <ArduinoJson.h>
#include <M5StickCPlus2.h>
#include <Preferences.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <esp_system.h>
#include <esp_wifi.h>
#include <mbedtls/md.h>

namespace {
constexpr uint16_t port = 46224;
constexpr uint32_t staleAfter = 1500;
constexpr size_t maxPacket = 900;
constexpr char hexDigits[] = "0123456789abcdef";
WiFiUDP udp;
IPAddress core;
String deviceId, name, ssid, password, nonce, session, cue, next, serialLine;
uint8_t key[32]{};
uint8_t inside = 0, outside = 0;
uint32_t sequence = 0, lastState = 0, offeredAt = 0, lastSend = 0, lastDraw = 0;
uint32_t restartAt = 0;
uint32_t lastPing = 0, pendingPing = 0, lastPong = 0, telemetrySequence = 0;
uint32_t lastLinkCheck = 0;
int rtt = -1;
bool configured = false, udpReady = false, diagnostics = false, dirty = true, serialOverflow = false;
bool testMode = false;

String hex(const uint8_t* bytes, size_t size) {
  String result;
  result.reserve(size * 2);
  for (size_t i = 0; i < size; ++i) {
    result += hexDigits[bytes[i] >> 4];
    result += hexDigits[bytes[i] & 15];
  }
  return result;
}

int digit(char c) {
  return c >= '0' && c <= '9' ? c - '0' : (c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1);
}

bool unhex(const String& text, uint8_t* bytes, size_t size) {
  if (text.length() != size * 2) return false;
  for (size_t i = 0; i < size; ++i) {
    int high = digit(text[i * 2]), low = digit(text[i * 2 + 1]);
    if (high < 0 || low < 0) return false;
    bytes[i] = static_cast<uint8_t>((high << 4) | low);
  }
  return true;
}

bool decodeText(const String& field, String& result, size_t limit) {
  uint8_t bytes[161]{};
  const size_t length = field.length() / 2;
  if (length > limit || !unhex(field, bytes, length)) return false;
  for (size_t i = 0; i < length; ++i) {
    if (bytes[i] < 32) return false;
  }
  result = reinterpret_cast<const char*>(bytes);
  return true;
}

String tag(const String& body) {
  uint8_t digest[32];
  const auto* sha256 = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  mbedtls_md_hmac(sha256, key, sizeof(key), reinterpret_cast<const uint8_t*>(body.c_str()), body.length(), digest);
  return hex(digest, sizeof(digest));
}

bool equalTag(const String& actual, const String& expected) {
  if (actual.length() != 64 || expected.length() != 64) return false;
  uint8_t difference = 0;
  for (size_t i = 0; i < 64; ++i) difference |= actual[i] ^ expected[i];
  return difference == 0;
}

void newSession() {
  uint8_t random[16];
  esp_fill_random(random, sizeof(random));
  nonce = hex(random, sizeof(random));
  session = "";
  sequence = 0;
  lastSend = 0;
  lastPing = pendingPing = lastPong = telemetrySequence = 0;
  rtt = -1;
  inside = outside = 0;
  testMode = false;
  cue = next = "";
  dirty = true;
}

void send(const String& body) {
  const String packet = body + "|" + tag(body);
  if (udp.beginPacket(core, port)) {
    udp.write(reinterpret_cast<const uint8_t*>(packet.c_str()), packet.length());
    udp.endPacket();
  }
}

void receive(uint32_t now) {
  // An input burst must leave time for USB and display handling.
  for (int i = 0; i < 8; ++i) {
    int length = udp.parsePacket();
    if (length <= 0) return;
    if (length > maxPacket || udp.remoteIP() != core || udp.remotePort() != port) {
      udp.flush();
      continue;
    }
    char buffer[maxPacket + 1];
    if (udp.read(buffer, length) != length) continue;
    buffer[length] = 0;
    const String packet(buffer);
    int boundary = packet.lastIndexOf('|');
    if (boundary < 0) continue;
    const String body = packet.substring(0, boundary);
    if (!equalTag(packet.substring(boundary + 1), tag(body))) continue;
    String fields[10];
    size_t count = 0;
    int start = 0;
    for (; count < 10; ++count) {
      int end = body.indexOf('|', start);
      fields[count] = end < 0 ? body.substring(start) : body.substring(start, end);
      if (end < 0) { ++count; break; }
      start = end + 1;
    }
    if (count < 3 || fields[0] != "TP1" || fields[2] != deviceId) continue;
    if (count == 5 && fields[1] == "offer" && fields[3] == nonce && session.isEmpty()) {
      uint8_t sessionBytes[16];
      if (!unhex(fields[4], sessionBytes, sizeof(sessionBytes))) continue;
      session = fields[4];
      offeredAt = now;
      send("TP1|ready|" + deviceId + "|" + nonce + "|" + session);
      lastSend = now;
    }
    if (count == 5 && fields[1] == "pong" && !session.isEmpty() && fields[3] == session &&
        pendingPing && fields[4] == String(pendingPing) && now - pendingPing <= 5000) {
      rtt = now - pendingPing;
      lastPong = now;
      pendingPing = 0;
      dirty = true;
    }
    if ((count != 9 && count != 10) || fields[1] != "state" || session.isEmpty() || fields[3] != session) continue;
    char* end = nullptr;
    unsigned long number = strtoul(fields[4].c_str(), &end, 10);
    if (*end || number <= sequence || fields[5].length() != 1 || fields[6].length() != 1) continue;
    int newInside = digit(fields[5][0]), newOutside = digit(fields[6][0]);
    String newCue, newNext;
    if (newInside < 0 || newInside > 3 || newOutside < 0 || newOutside > 3 ||
        !decodeText(fields[7], newCue, 160) || !decodeText(fields[8], newNext, 80)) continue;
    bool simulation = count == 10 && fields[9] == "test";
    dirty |= sequence == 0 || newInside != inside || newOutside != outside || cue != newCue ||
        next != newNext || simulation != testMode;
    testMode = simulation;
    sequence = number;
    lastState = now;
    inside = newInside;
    outside = newOutside;
    cue = newCue;
    next = newNext;
  }
}

void network(uint32_t now) {
  if (WiFi.status() != WL_CONNECTED) {
    if (udpReady) {
      udp.stop();
      udpReady = false;
      newSession();
    }
    return;
  }
  if (!configured) return;
  if (!udpReady) {
    udpReady = udp.begin(0);
    if (!udpReady) return;
    newSession();
  }
  if (!session.isEmpty() && now - (sequence ? lastState : offeredAt) >= staleAfter) newSession();
  receive(now);
  const bool live = sequence > 0;
  if (live && (lastPing == 0 || now - lastPing >= 2000)) {
    pendingPing = lastPing = now;
    send("TP1|ping|" + deviceId + "|" + session + "|" + String(pendingPing));
  }
  if (rtt >= 0 && now - lastPong > 6000) {
    rtt = -1;
    dirty = true;
  }
  if (lastSend != 0 && now - lastSend < (live ? 1000 : 350)) return;
  if (session.isEmpty()) {
    send("TP1|hello|" + deviceId + "|" + nonce);
  } else if (!live) {
    send("TP1|ready|" + deviceId + "|" + nonce + "|" + session);
  } else {
    send("TP1|link|" + deviceId + "|" + session + "|" + String(sequence) + "|" +
         String(StickCP2.Power.getBatteryLevel()) + "|" + String(WiFi.RSSI()) + "|" +
         String(rtt) + "|" + String(now - lastState) + "|" + String(++telemetrySequence));
  }
  lastSend = now;
}

bool settings(JsonDocument& doc, bool persist) {
  String host = doc["host"] | "";
  String deviceName = doc["name"] | "";
  String networkName = doc["ssid"] | "";
  String networkPassword = doc["password"] | "";
  String secret = doc["key"] | "";
  IPAddress address;
  uint8_t bytes[32];
  if (!address.fromString(host.c_str()) || static_cast<uint32_t>(address) == 0 ||
      !unhex(secret, bytes, sizeof(bytes)) || deviceName.isEmpty() || deviceName.length() > 48 ||
      networkName.length() > 32 || networkPassword.length() > 64 ||
      (networkName.isEmpty() && !networkPassword.isEmpty())) return false;
  if (persist) {
    Preferences prefs;
    if (!prefs.begin("tallypilot", false)) return false;
    // An empty WLAN form keeps the credentials used by the current firmware.
    // This also preserves the pilot's profile when upgrading from 0.2.
    if (networkName.isEmpty()) {
      JsonDocument previous;
      if (!deserializeJson(previous, prefs.getString("config", ""))) {
        doc["ssid"] = String(previous["ssid"] | "");
        doc["password"] = String(previous["password"] | "");
      }
    }
    String encoded;
    serializeJson(doc, encoded);
    bool saved = prefs.putString("config", encoded) == encoded.length();
    prefs.end();
    return saved;
  }
  memcpy(key, bytes, sizeof(bytes));
  core = address;
  name = deviceName;
  ssid = networkName;
  password = networkPassword;
  return true;
}

void status() {
  JsonDocument doc;
  doc["version"] = TALLY_PAGER_VERSION;
  doc["id"] = deviceId;
  doc["configured"] = configured;
  doc["wifi"] = WiFi.status() == WL_CONNECTED;
  doc["ip"] = WiFi.localIP().toString();
  doc["core"] = sequence != 0;
  doc["link"] = sequence == 0 ? "disconnected" : (millis() - lastState >= 750 || rtt >= 250 ? "delayed" : "connected");
  doc["rssi"] = WiFi.status() == WL_CONNECTED ? WiFi.RSSI() : 0;
  doc["rtt"] = rtt;
  doc["test"] = testMode;
  doc["inside"] = inside;
  doc["outside"] = outside;
  doc["cue"] = cue;
  doc["next"] = next;
  Serial.print("TP ");
  serializeJson(doc, Serial);
  Serial.println();
}

void serialInput() {
  for (int i = 0; i < 256 && Serial.available(); ++i) {
    char c = Serial.read();
    if (c == '\r') continue;
    if (c != '\n') {
      if (serialLine.length() < 1024 && !serialOverflow) serialLine += c;
      else serialOverflow = true;
      continue;
    }
    if (!serialOverflow && serialLine == "TP STATUS") status();
    if (!serialOverflow && serialLine.startsWith("TP CONFIG ")) {
      JsonDocument doc;
      bool ok = !deserializeJson(doc, serialLine.c_str() + 10) && settings(doc, true);
      Serial.println(ok ? "TP OK" : "TP ERROR config");
      if (ok) restartAt = millis() + 350;
    }
    serialLine = "";
    serialOverflow = false;
  }
}

const uint16_t colors[] = {0x4208, 0x1C9F, 0x0640, 0xF904};
const char* states[] = {"?", "SAFE", "PREVIEW", "LIVE"};

int qualityBars() {
  if (WiFi.status() != WL_CONNECTED) return -1;
  int rssi = WiFi.RSSI();
  return (rssi >= -82) + (rssi >= -75) + (rssi >= -67) + (rssi >= -55);
}

bool linkDelayed() {
  return sequence && (millis() - lastState >= 750 || rtt >= 250);
}

String displayText(String text) {
  text.replace("ä", "ae"); text.replace("ö", "oe"); text.replace("ü", "ue");
  text.replace("Ä", "Ae"); text.replace("Ö", "Oe"); text.replace("Ü", "Ue"); text.replace("ß", "ss");
  return text;
}

void draw() {
  auto& display = StickCP2.Display;
  display.setTextDatum(top_left);
  display.setTextSize(1);
  display.startWrite();
  if (diagnostics) {
    display.fillScreen(TFT_BLACK);
    display.setTextColor(TFT_WHITE);
    display.drawString("TallyPager " TALLY_PAGER_VERSION, 5, 6);
    display.drawString("Stick: " + deviceId, 5, 24);
    display.drawString("WLAN: " + WiFi.localIP().toString(), 5, 42);
    display.drawString("Core: " + core.toString(), 5, 60);
    display.drawString(rtt < 0 ? "Core RTT: --" : "Core RTT: " + String(rtt) + " ms", 5, 78);
    display.drawString("RSSI: " + String(WiFi.RSSI()) + " dBm", 5, 96);
    display.drawString(String("Akku: ") + StickCP2.Power.getBatteryLevel() + "%", 5, 114);
  } else {
    display.fillScreen(colors[outside]);
    display.fillRect(6, 18, 228, 100, colors[inside]);
    display.setTextColor(TFT_WHITE);
    String headline = next.isEmpty() ? name : "next: " + displayText(next);
    if (testMode) headline = "TEST " + headline;
    display.drawString(headline.substring(0, 19), 5, 5);
    const int bars = qualityBars();
    const bool delayed = linkDelayed();
    display.fillRect(127, 0, 113, 18, TFT_BLACK);
    for (int i = 0; i < 4; ++i) {
      int height = 3 + 2 * i;
      display.fillRect(128 + i * 5, 14 - height, 3, height, i < bars ? TFT_GREEN : 0x4208);
    }
    display.setTextColor(!sequence ? TFT_LIGHTGREY : delayed ? TFT_ORANGE : TFT_WHITE);
    String connection = !sequence ? "Getrennt" : delayed ? "Delayed" : "Verbunden";
    if (rtt >= 0) connection += " " + String(rtt) + "ms";
    display.drawString(connection, 148, 5);
    display.setTextColor(TFT_WHITE);
    display.drawString(String("Aussen: ") + states[outside], 6, 123);
    display.drawString(String("Innen: ") + states[inside], 12, 102);
    if (!configured || sequence == 0) {
      const char* message = !configured ? "USB-Setup starten" :
          (WiFi.status() == WL_CONNECTED ? "Core offline" : "WLAN nicht verbunden");
      display.drawString(message, 12, 38);
    } else {
      display.setTextColor(inside >= 2 ? TFT_BLACK : TFT_WHITE);
      display.setCursor(12, 27);
      display.setTextSize(2);
      String text = displayText(cue);
      // Use a fixed number of cells so long words stay inside the tally field.
      for (unsigned int i = 0; i < min(text.length(), 17u * 4u); i += 17) {
        display.drawString(text.substring(i, i + 17), 12, 28 + (i / 17) * 17);
      }
    }
  }
  display.endWrite();
}
}

void setup() {
  Serial.begin(115200);
  StickCP2.begin(M5.config());
  StickCP2.Display.setRotation(1);
  StickCP2.Display.setBrightness(160);
  const uint64_t mac = ESP.getEfuseMac();
  char id[13];
  snprintf(id, sizeof(id), "%012llx", static_cast<unsigned long long>(mac));
  deviceId = id;
  name = "TallyPager";
  Preferences prefs;
  if (prefs.begin("tallypilot", true)) {
    String json = prefs.getString("config", "");
    prefs.end();
    JsonDocument doc;
    configured = !deserializeJson(doc, json) && settings(doc, false);
  }
  if (configured) {
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);
    WiFi.setAutoReconnect(true);
    if (ssid.isEmpty()) {
      WiFi.begin();
    } else {
      esp_wifi_set_storage(WIFI_STORAGE_RAM);
      WiFi.begin(ssid.c_str(), password.c_str());
      password = "";
    }
  }
  status();
}

void loop() {
  M5.update();
  serialInput();
  uint32_t now = millis();
  if (restartAt && static_cast<int32_t>(now - restartAt) >= 0) ESP.restart();
  network(now);
  if (M5.BtnA.wasClicked()) {
    diagnostics = !diagnostics;
    dirty = true;
  }
  if (now - lastLinkCheck >= 200) {
    lastLinkCheck = now;
    static int previousBars = -2;
    static bool previousDelay = false;
    int bars = qualityBars();
    bool delayed = linkDelayed();
    dirty |= bars != previousBars || delayed != previousDelay;
    previousBars = bars;
    previousDelay = delayed;
  }
  if (dirty || (diagnostics && now - lastDraw >= 1000)) {
    draw();
    lastDraw = now;
    dirty = false;
  }
  delay(1);
}
