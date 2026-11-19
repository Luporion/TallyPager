#include <Arduino.h>
#include <ArduinoJson.h>
#include <EEPROM.h>
#include <ESP8266WiFi.h>
extern "C" {
#include <espnow.h>
#include <user_interface.h>
}
#include "radio_frame.h"

namespace {
using namespace radio_frame;
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
uint8_t part = 0, channel = 6, ownAddress[6];
volatile int sendResult = 0;
uint32_t sentAt = 0, restartAt = 0;
bool waiting = false, ready = false, overflow = false;
char line[packetLimit + 22];
size_t used = 0;

String mac(const uint8_t* address) {
  char encoded[13];
  snprintf(encoded, sizeof(encoded), "%02x%02x%02x%02x%02x%02x",
      address[0], address[1], address[2], address[3], address[4], address[5]);
  return encoded;
}

bool parseMac(const char* text, uint8_t* address) {
  bool nonzero = false;
  for (unsigned i = 0; i < 6; ++i) {
    auto digit = [](char c) { return c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1; };
    int high = digit(text[2 * i]), low = digit(text[2 * i + 1]);
    if (high < 0 || low < 0) return false;
    address[i] = (high << 4) | low;
    nonzero |= address[i] != 0;
  }
  return nonzero && !(address[0] & 1);
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
  JsonDocument doc;
  doc["version"] = TALLY_PAGER_VERSION;
  doc["id"] = mac(ownAddress);
  doc["board"] = "esp8266";
  doc["configured"] = true;
  doc["baseRole"] = true;
  doc["mode"] = "espnow";
  doc["radioAddress"] = mac(ownAddress);
  doc["channel"] = channel;
  doc["radioReady"] = ready;
  auto features = doc["features"].to<JsonArray>();
  features.add("radio-base");
  features.add("espnow");
  Serial.print("TP ");
  serializeJson(doc, Serial);
  Serial.println();
}

void command() {
  line[used] = 0;
  if (strcmp(line, "TP STATUS") == 0) { status(); return; }
  if (strncmp(line, "TP BASE ", 8) == 0) {
    JsonDocument doc;
    if (deserializeJson(doc, line + 8) || doc["mode"] != "espnow" || !doc["channel"].is<int>() ||
        doc["channel"].as<int>() < 1 || doc["channel"].as<int>() > 13) {
      Serial.println("TP ERROR mode");
      return;
    }
    uint8_t requested = doc["channel"].as<int>();
    EEPROM.write(0, 0x54);
    EEPROM.write(1, requested);
    if (!EEPROM.commit()) { Serial.println("TP ERROR eeprom"); return; }
    Serial.println("TP OK");
    restartAt = millis() + 350;
    return;
  }
  if (!ready || used <= 21 || strncmp(line, "TP LINK ", 8) != 0 || line[20] != ' ' ||
      strncmp(line + 21, "TP1|", 4) != 0) return;
  unsigned next = (txHead + 1) % (sizeof(outgoing) / sizeof(outgoing[0]));
  if (next == txTail) return;
  auto& packet = outgoing[txHead];
  if (!parseMac(line + 8, packet.target)) return;
  packet.length = used - 21;
  memcpy(packet.payload, line + 21, packet.length);
  txHead = next;
}

void radioTick() {
  while (rxTail != rxHead) {
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
  Serial.setRxBufferSize(2048);
  Serial.begin(115200);
  EEPROM.begin(4);
  uint8_t stored = EEPROM.read(1);
  if (EEPROM.read(0) == 0x54 && stored >= 1 && stored <= 13) channel = stored;
  WiFi.persistent(false);
  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(false);
  WiFi.disconnect();
  WiFi.setSleepMode(WIFI_NONE_SLEEP);
  WiFi.macAddress(ownAddress);
  packetId = os_random();
  ready = wifi_set_channel(channel) && esp_now_init() == 0 &&
      esp_now_set_self_role(ESP_NOW_ROLE_COMBO) == 0 &&
      esp_now_register_recv_cb(receiveFrame) == 0 &&
      esp_now_register_send_cb([](uint8_t*, uint8_t result) { sendResult = result == 0 ? 1 : -1; }) == 0;
}

void loop() {
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\r') continue;
    if (ch == '\n') {
      if (used && !overflow) command();
      used = 0;
      overflow = false;
    } else if (ch < ' ' || used >= sizeof(line) - 1) overflow = true;
    else if (!overflow) line[used++] = ch;
  }
  if (ready) radioTick();
  if (restartAt && static_cast<int32_t>(millis() - restartAt) >= 0) ESP.restart();
  delay(0);
}
