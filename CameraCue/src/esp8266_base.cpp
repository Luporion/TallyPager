#include <Arduino.h>
#include <ArduinoJson.h>
#include <EEPROM.h>
#include <ESP8266WiFi.h>
#include "hotspot_link.h"
extern "C" {
#include <espnow.h>
#include <user_interface.h>
}
#include "radio_frame.h"

namespace {
using namespace radio_frame;
enum class Mode : uint8_t { EspNow, Hotspot, Both };
Mode mode = Mode::EspNow;
constexpr size_t automaticLength = 16, maxPasswordLength = 63;
constexpr int passwordStored = 3, passwordOffset = 4;
// Keep the original key and layout so automatic mode and older firmware can still use it.
constexpr int securityOffset = passwordOffset + automaticLength;
constexpr int customLengthOffset = securityOffset + 1, customPasswordOffset = customLengthOffset + 1;
constexpr int securitySize = 2 + maxPasswordLength;
enum class Security : uint8_t { Automatic = 0xff, Custom = 0x50, Open = 0x4f };
Security security = Security::Automatic;
hotspot::Link wifiLink;
char wifiPassword[maxPasswordLength + 1]{};
String wifiName;
bool wifiReady = false, espNowReady = false;
constexpr unsigned receiveSlots = 17;
struct ReceivedFrame {
  uint8_t sender[6], bytes[headerSize + fragmentSize], length;
};
ReceivedFrame received[receiveSlots];
volatile unsigned rxHead = 0, rxTail = 0;
Assembly assemblies[6];

struct Packet {
  uint8_t target[6];
  uint16_t length;
  uint8_t payload[packetLimit];
};
Packet outgoing[12];
unsigned txHead = 0, txTail = 0;
uint16_t packetId;
uint8_t part = 0, channel = 6, deviceAddress[6], nowAddress[6];
volatile int sendResult = 0;
uint32_t sentAt = 0, restartAt = 0;
bool waiting = false, overflow = false;
char line[hotspot::envelopeSize + packetLimit + 1];
size_t used = 0;

String mac(const uint8_t* address) {
  char encoded[13];
  snprintf(encoded, sizeof(encoded), "%02x%02x%02x%02x%02x%02x",
      address[0], address[1], address[2], address[3], address[4], address[5]);
  return encoded;
}

bool parseMac(const char* text, uint8_t* address) {
  if (!hotspot::decodeAddress(text, address)) return false;
  bool nonzero = false;
  for (unsigned i = 0; i < 6; ++i) nonzero |= address[i] != 0;
  return nonzero && !(address[0] & 1);
}

bool hasHotspot() { return mode != Mode::EspNow; }
bool hasEspNow() { return mode != Mode::Hotspot; }
const char* modeName() {
  return mode == Mode::Both ? "espnow-hotspot" : mode == Mode::Hotspot ? "hotspot" : "espnow";
}

bool validPassword(const char* password, size_t length) {
  if (!password || length < 8 || length > maxPasswordLength) return false;
  for (size_t i = 0; i < length; ++i)
    if (password[i] < ' ' || password[i] > '~') return false;
  return true;
}

const char* securityName() {
  return security == Security::Custom ? "custom" : security == Security::Open ? "open" : "automatic";
}

bool loadPassword() {
  auto stored = static_cast<Security>(EEPROM.read(securityOffset));
  if (stored == Security::Open) {
    security = stored;
    return true;
  }
  if (stored == Security::Custom) {
    size_t length = EEPROM.read(customLengthOffset);
    if (length <= maxPasswordLength) {
      for (size_t i = 0; i < length; ++i) wifiPassword[i] = EEPROM.read(customPasswordOffset + i);
      if (validPassword(wifiPassword, length)) {
        wifiPassword[length] = '\0';
        security = stored;
        return true;
      }
    }
  }
  wifiPassword[automaticLength] = '\0';
  if (EEPROM.read(passwordStored) == 0x58) {
    bool valid = true;
    for (size_t i = 0; i < automaticLength; ++i) {
      char ch = EEPROM.read(passwordOffset + i);
      valid &= (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
      wifiPassword[i] = ch;
    }
    if (valid) return true;
  }
  uint8_t random[automaticLength / 2];
  ESP.random(random, sizeof(random));
  constexpr char hex[] = "0123456789abcdef";
  for (size_t i = 0; i < automaticLength; ++i) {
    wifiPassword[i] = hex[(random[i / 2] >> (i % 2 ? 0 : 4)) & 15];
    EEPROM.write(passwordOffset + i, wifiPassword[i]);
  }
  EEPROM.write(passwordStored, 0x58);
  if (EEPROM.commit()) return true;
  wifiPassword[0] = '\0';
  return false;
}

void configureSecurity(const char* json) {
  JsonDocument doc;
  if (!hasHotspot() || restartAt || deserializeJson(doc, json)) { Serial.println("TP ERROR security"); return; }
  auto name = doc["security"].as<JsonString>();
  auto password = doc["password"].as<JsonString>();
  Security requested;
  if (name == "automatic") requested = Security::Automatic;
  else if (name == "open") requested = Security::Open;
  else if (name == "custom" && validPassword(password.c_str(), password.size())) requested = Security::Custom;
  else { Serial.println("TP ERROR security"); return; }

  uint8_t backup[securitySize];
  for (int i = 0; i < securitySize; ++i) backup[i] = EEPROM.read(securityOffset + i);
  size_t length = requested == Security::Custom ? password.size() : 0;
  EEPROM.write(securityOffset, static_cast<uint8_t>(requested));
  EEPROM.write(customLengthOffset, length);
  for (size_t i = 0; i < maxPasswordLength; ++i)
    EEPROM.write(customPasswordOffset + i, i < length ? password.c_str()[i] : 0);
  if (!EEPROM.commit()) {
    for (int i = 0; i < securitySize; ++i) EEPROM.write(securityOffset + i, backup[i]);
    Serial.println("TP ERROR eeprom");
    return;
  }
  Serial.println("TP OK");
  restartAt = millis() + 350;
}

void receiveFrame(uint8_t* address, uint8_t* bytes, uint8_t length) {
  unsigned next = (rxHead + 1) % receiveSlots;
  if (next == rxTail || length <= headerSize || length > headerSize + fragmentSize) return;
  auto& frame = received[rxHead];
  memcpy(frame.sender, address, 6);
  memcpy(frame.bytes, bytes, length);
  frame.length = length;
  rxHead = next;
}

void status() {
  bool ready = (!hasEspNow() || espNowReady) && (!hasHotspot() || wifiReady);
  JsonDocument doc;
  doc["version"] = TALLY_PAGER_VERSION;
  doc["id"] = mac(deviceAddress);
  doc["board"] = "esp8266";
  doc["configured"] = ready;
  doc["baseRole"] = true;
  doc["mode"] = modeName();
  if (espNowReady) doc["radioAddress"] = mac(nowAddress);
  doc["channel"] = channel;
  doc["radioReady"] = ready;
  if (hasHotspot()) doc["hotspotSecurity"] = securityName();
  if (wifiReady) {
    doc["ip"] = hotspot::gateway.toString();
    doc["hotspotSsid"] = wifiName;
    doc["hotspotPassword"] = wifiPassword;
  }
  auto features = doc["features"].to<JsonArray>();
  features.add("radio-base");
  features.add("espnow");
  features.add("hotspot");
  features.add("espnow-hotspot");
  features.add("hotspot-security");
  Serial.print("TP ");
  serializeJson(doc, Serial);
  Serial.println();
}

void command() {
  line[used] = 0;
  if (strcmp(line, "TP STATUS") == 0) { status(); return; }
  if (strncmp(line, "TP AP ", 6) == 0) { configureSecurity(line + 6); return; }
  if (strncmp(line, "TP BASE ", 8) == 0) {
    JsonDocument doc;
    if (deserializeJson(doc, line + 8) || !doc["channel"].is<int>()) { Serial.println("TP ERROR mode"); return; }
    const char* name = doc["mode"] | "";
    Mode requestedMode;
    if (!strcmp(name, "espnow")) requestedMode = Mode::EspNow;
    else if (!strcmp(name, "hotspot")) requestedMode = Mode::Hotspot;
    else if (!strcmp(name, "espnow-hotspot")) requestedMode = Mode::Both;
    else { Serial.println("TP ERROR mode"); return; }
    int requested = doc["channel"].as<int>();
    if (requested < 1 || requested > 13 || (requestedMode != Mode::EspNow && !hotspot::validChannel(requested))) {
      Serial.println("TP ERROR channel"); return;
    }
    EEPROM.write(0, 0x54);
    EEPROM.write(1, requested);
    EEPROM.write(2, static_cast<uint8_t>(requestedMode));
    if (!EEPROM.commit()) { Serial.println("TP ERROR eeprom"); return; }
    Serial.println("TP OK");
    restartAt = millis() + 350;
    return;
  }
  const char* wifiPrefix = mode == Mode::Both ? hotspot::wifiPrefix : hotspot::linkPrefix;
  if (wifiReady && strncmp(line, wifiPrefix, 8) == 0) { wifiLink.fromSerial(line, used, wifiPrefix); return; }
  if (!espNowReady || used <= hotspot::envelopeSize || strncmp(line, hotspot::linkPrefix, 8) != 0 || line[20] != ' ' ||
      !hotspot::tallyPayload(line + hotspot::envelopeSize, used - hotspot::envelopeSize)) return;
  unsigned next = (txHead + 1) % (sizeof(outgoing) / sizeof(outgoing[0]));
  if (next == txTail) return;
  auto& packet = outgoing[txHead];
  if (!parseMac(line + 8, packet.target)) return;
  packet.length = used - hotspot::envelopeSize;
  memcpy(packet.payload, line + hotspot::envelopeSize, packet.length);
  txHead = next;
}

void radioTick() {
  for (int i = 0; i < 8 && rxTail != rxHead; ++i) {
    auto& frame = received[rxTail];
    if (auto packet = receive(assemblies, frame.sender, frame.bytes, frame.length, millis()); packet &&
        strncmp(packet, "TP1|", 4) == 0 && !strpbrk(packet, "\r\n")) {
      Serial.print("TP LINK ");
      Serial.print(mac(frame.sender));
      Serial.print(' ');
      Serial.println(packet);
    }
    rxTail = (rxTail + 1) % receiveSlots;
  }
  if (txTail == txHead) return;
  auto& packet = outgoing[txTail];
  if (waiting) {
    int result = sendResult;
    if (!result && millis() - sentAt < 300) return;
    waiting = false;
    if (result <= 0 || ++part * fragmentSize >= packet.length) {
      part = 0;
      txTail = (txTail + 1) % (sizeof(outgoing) / sizeof(outgoing[0]));
      return;
    }
  }
  if (!part) ++packetId;
  uint8_t frame[headerSize + fragmentSize];
  size_t length = encode(packet.payload, packet.length, packetId, part, frame);
  sendResult = 0;
  if ((esp_now_is_peer_exist(packet.target) ||
      esp_now_add_peer(packet.target, ESP_NOW_ROLE_COMBO, channel, nullptr, 0) == 0) &&
      esp_now_send(packet.target, frame, length) == 0) {
    waiting = true;
    sentAt = millis();
  } else {
    part = 0;
    txTail = (txTail + 1) % (sizeof(outgoing) / sizeof(outgoing[0]));
  }
}
}

void setup() {
  EEPROM.begin(securityOffset + securitySize);
  uint8_t stored = EEPROM.read(1);
  if (EEPROM.read(0) == 0x54) {
    uint8_t savedMode = EEPROM.read(2);
    if (savedMode <= static_cast<uint8_t>(Mode::Both)) mode = static_cast<Mode>(savedMode);
    if (stored >= 1 && stored <= 13 && (!hasHotspot() || hotspot::validChannel(stored))) channel = stored;
  }
  Serial.setRxBufferSize(hasHotspot() ? 4096 : 2048);
  Serial.begin(hasHotspot() ? 460800 : 115200);
  WiFi.persistent(false);
  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(false);
  WiFi.disconnect();
  WiFi.setSleepMode(WIFI_NONE_SLEEP);
  WiFi.macAddress(deviceAddress);
  memcpy(nowAddress, deviceAddress, sizeof(nowAddress));
  if (hasHotspot()) {
    wifiName = "TallyPager-" + mac(deviceAddress).substring(6);
    wifiReady = loadPassword() && WiFi.mode(WIFI_AP) && WiFi.softAPConfig(hotspot::gateway, hotspot::gateway, hotspot::mask) &&
        WiFi.softAP(wifiName.c_str(), security == Security::Open ? nullptr : wifiPassword,
            channel, false, hotspot::maxClients) && wifiLink.begin();
    if (wifiReady) WiFi.softAPmacAddress(nowAddress);
    else WiFi.mode(WIFI_OFF);
  }
  packetId = os_random();
  espNowReady = hasEspNow() && (hasHotspot() ? wifiReady && wifi_get_channel() == channel : wifi_set_channel(channel)) &&
      esp_now_init() == 0 &&
      esp_now_set_self_role(ESP_NOW_ROLE_COMBO) == 0 &&
      esp_now_register_recv_cb(receiveFrame) == 0 &&
      esp_now_register_send_cb([](uint8_t*, uint8_t result) { sendResult = result == 0 ? 1 : -1; }) == 0;
}

void loop() {
  for (int i = 0; i < 2048 && Serial.available(); ++i) {
    char ch = Serial.read();
    if (ch == '\r') continue;
    if (ch == '\n') {
      if (used && !overflow) command();
      used = 0;
      overflow = false;
    } else if (ch < ' ' || used >= sizeof(line) - 1) overflow = true;
    else if (!overflow) line[used++] = ch;
  }
  if (espNowReady) radioTick();
  if (wifiReady) wifiLink.toSerial(mode == Mode::Both ? hotspot::wifiPrefix : hotspot::linkPrefix);
  if (restartAt && static_cast<int32_t>(millis() - restartAt) >= 0) ESP.restart();
  delay(0);
}
