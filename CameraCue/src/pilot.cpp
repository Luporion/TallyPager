#include <Arduino.h>
#include <ArduinoJson.h>
#include <M5StickCPlus2.h>
#include <Preferences.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include <esp_system.h>
#include <esp_wifi.h>
#include <mbedtls/md.h>
#include <mbedtls/gcm.h>
#include <atomic>
#include "radio.h"

namespace {
constexpr uint16_t port = 46224;
constexpr uint16_t managementPort = 46226;
constexpr uint32_t staleAfter = 1500;
constexpr size_t maxPacket = 900;
constexpr char hexDigits[] = "0123456789abcdef";
constexpr const char* firmwareFeatures[] = {"usb-config", "chat-ack", "link-stats", "diagnostics",
    "wifi-config", "wifi-discovery", "radio-base", "espnow", "espnow-lr", "ble",
    "usb-read-settings", "clock", "device-slots", "base-preview", "base-preview-details", "remote-slots", "presentation"};
WiFiUDP udp, management;
IPAddress core;
String deviceId, name, ssid, password, nonce, session, cue, next, serialLine;
String mode = "udp", baseMac, radioMac;
bool baseRole = false;
uint8_t gatewayAddress[6]{};
int radioChannel = 6;
String lastConfigId, configResult = "idle";
uint32_t configCounter = 0, configSwitchAt = 0, configDeadline = 0;
String managementSession, discoverNonce;
IPAddress managementPc;
uint32_t managementTime = 0, lastManagement = 0, managementCounter = 0;
bool managementReady = false;
bool wifiTrial = false, changeWifi = false;
JsonDocument oldSettings, proposedSettings;
String insideSource = "off", outsideSource = "off";
String chatId, shownId, pendingAck, confirmedAck;
bool requireAck = false;
std::atomic<uint32_t> wifiLoss{0}, wifiLostAt{0}, wifiDowntime{0};
std::atomic<int> wifiReason{0};
uint32_t snapshotLoss = 0, lastStaleAge = 0, missedStates = 0, lastDiagnostic = 0;
uint32_t snapshotLostAt = 0, snapshotDowntime = 0;
uint8_t key[32]{};
uint8_t inside = 0, outside = 0;
uint32_t sequence = 0, lastState = 0, offeredAt = 0, lastSend = 0, lastDraw = 0;
uint32_t restartAt = 0;
uint32_t lastPing = 0, pendingPing = 0, lastPong = 0, telemetrySequence = 0;
uint32_t lastLinkCheck = 0;
uint32_t connectedAt = 0;
int rtt = -1;
bool configured = false, udpReady = false, diagnostics = false, dirty = true, serialOverflow = false;
bool testMode = false;
int textMode = 0;
bool beamerLive = false;
String insideLive, outsideLive;
uint32_t presentationSequence = 0, presentationAt = 0;
int activeSlot = 0, chosenSlot = -1;
String slotRequest, slotOutcome, trialConfig;
uint32_t trialDeadline = 0;
uint32_t menuHeld = 0, menuTouched = 0, clockAt = 0, clockSeconds = 0;
int lastMinute = -1;
bool clockSynced = false, menuArmed = false, menuAttempted = false;
struct BaseLine {
  String name, text, badge;
  uint8_t inside = 0, outside = 0;
  bool online = false, insideEnabled = true, outsideEnabled = true;
};
BaseLine baseLines[4];
size_t baseCount = 0;
uint32_t baseViewAt = 0;

void reportConfig();
void handleConfig(String* fields, size_t count, uint32_t now, uint32_t& lastCounter);
void updateConfig(uint32_t now);
void reportSlots();
void handleSlot(const String& request, const String& slot, uint32_t now);
void updateSlotTrial(uint32_t now);

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

size_t verifyFields(const char* data, String (&fields)[14]) {
  const String packet(data);
  int boundary = packet.lastIndexOf('|');
  if (boundary < 0) return 0;
  const String body = packet.substring(0, boundary);
  if (!equalTag(packet.substring(boundary + 1), tag(body))) return 0;
  int start = 0;
  for (size_t count = 0; count < 14; ++count) {
    int end = body.indexOf('|', start);
    fields[count] = end < 0 ? body.substring(start) : body.substring(start, end);
    if (end < 0)
      return count >= 2 && fields[0] == "TP1" && fields[2] == deviceId ? count + 1 : 0;
    start = end + 1;
  }
  return 0;
}

void newSession() {
  uint8_t random[16];
  esp_fill_random(random, sizeof(random));
  nonce = hex(random, sizeof(random));
  session = "";
  sequence = 0;
  connectedAt = 0;
  lastSend = 0;
  lastPing = pendingPing = lastPong = telemetrySequence = 0;
  rtt = -1;
  inside = outside = 0;
  testMode = false;
  textMode = 0;
  beamerLive = false;
  insideLive = outsideLive = "";
  presentationSequence = 0;
  presentationAt = 0;
  cue = next = "";
  chatId = shownId = "";
  requireAck = false;
  lastDiagnostic = 0;
  configCounter = 0;
  dirty = true;
}

void sendUdp(WiFiUDP& socket, const IPAddress& destination, const String& body) {
  const String packet = body + "|" + tag(body);
  if (socket.beginPacket(destination, port)) {
    socket.write(reinterpret_cast<const uint8_t*>(packet.c_str()), packet.length());
    socket.endPacket();
  }
}

void send(const String& body) {
  if (mode == "udp") { sendUdp(udp, core, body); return; }
  const String packet = body + "|" + tag(body);
  radio::send(gatewayAddress, packet);
}

String firmwareMessage(const String& currentSession) {
  String body = "TP1|firmware|" + deviceId + "|" + currentSession + "|" TALLY_PAGER_VERSION "|";
  for (size_t i = 0; i < sizeof(firmwareFeatures) / sizeof(firmwareFeatures[0]); ++i) {
    if (i > 0) body += ',';
    body += firmwareFeatures[i];
  }
  return body;
}

void receive(uint32_t now) {
  // An input burst must leave time for USB and display handling.
  for (int i = 0; i < 8; ++i) {
    char buffer[maxPacket + 1];
    if (mode == "udp") {
      int length = udp.parsePacket();
      if (length <= 0) return;
      if (length > maxPacket || udp.remoteIP() != core || udp.remotePort() != port) {
        udp.flush();
        continue;
      }
      if (udp.read(buffer, length) != length) continue;
      buffer[length] = 0;
    } else {
      uint8_t address[6];
      if (!radio::read(address, buffer)) return;
    }
    String fields[14];
    size_t count = verifyFields(buffer, fields);
    if (count < 3) continue;
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
    if (session.isEmpty() || count < 5 || fields[3] != session) continue;
    if (count == 9 && fields[1] == "presentation") {
      char* end = nullptr;
      unsigned long number = strtoul(fields[4].c_str(), &end, 10);
      String first, second;
      if (end == fields[4].c_str() || *end || !sequence || number != sequence || number < presentationSequence ||
          (fields[5] != "0" && fields[5] != "1" && fields[5] != "2") ||
          (fields[6] != "0" && fields[6] != "1") ||
          !decodeText(fields[7], first, 144) || !decodeText(fields[8], second, 144)) continue;
      int selected = fields[5].toInt();
      bool beamer = fields[6] == "1";
      dirty |= textMode != selected || beamerLive != beamer || insideLive != first || outsideLive != second;
      textMode = selected;
      beamerLive = beamer;
      insideLive = first;
      outsideLive = second;
      presentationSequence = number;
      presentationAt = now;
      send("TP1|presented|" + deviceId + "|" + session + "|" + String(number));
      continue;
    }
    if (count == 6 && fields[1] == "switch_slot" && sequence) {
      handleSlot(fields[4], fields[5], now);
      continue;
    }
    if (count == 5 && fields[1] == "clock") {
      char* end = nullptr;
      const char* value = fields[4].c_str();
      unsigned long seconds = strtoul(value, &end, 10);
      if (end != value && !*end && seconds < 86400) {
        clockSeconds = seconds;
        clockAt = now;
        clockSynced = true;
        dirty = true;
      }
      continue;
    }
    if (mode == "udp" && fields[1] == "cfg" && sequence && trialConfig.isEmpty() && !restartAt) {
      handleConfig(fields, count, now, configCounter);
      continue;
    }
    if (count == 5 && fields[1] == "chat_ok" && !pendingAck.isEmpty() && fields[4] == pendingAck) {
      confirmedAck = pendingAck;
      pendingAck = "";
      dirty = true;
    }
    if ((count != 9 && count != 10 && count != 12 && count != 14) || fields[1] != "state") continue;
    char* end = nullptr;
    unsigned long number = strtoul(fields[4].c_str(), &end, 10);
    if (*end || number <= sequence || fields[5].length() != 1 || fields[6].length() != 1) continue;
    int newInside = digit(fields[5][0]), newOutside = digit(fields[6][0]);
    String newCue, newNext;
    if (newInside < 0 || newInside > 3 || newOutside < 0 || newOutside > 3 ||
        !decodeText(fields[7], newCue, 160) || !decodeText(fields[8], newNext, 80)) continue;
    bool simulation = count >= 10 && fields[9] == "test";
    String newInsideSource = count >= 12 ? fields[10] : "Innen";
    String newOutsideSource = count >= 12 ? fields[11] : "Aussen";
    String newChatId;
    if (count == 14) {
      uint8_t id[16];
      if (!unhex(fields[12], id, sizeof(id)) || (fields[13] != "0" && fields[13] != "1")) continue;
      newChatId = fields[12];
    }
    if (newInsideSource.length() > 13 || newOutsideSource.length() > 13) continue;
    if (sequence == 0) {
      connectedAt = now;
      if (snapshotLostAt) {
        snapshotDowntime = now - snapshotLostAt;
        snapshotLostAt = 0;
      }
    }
    bool newRequireAck = count == 14 && fields[13] == "1";
    dirty |= sequence == 0 || newInside != inside || newOutside != outside || cue != newCue ||
        next != newNext || simulation != testMode || chatId != newChatId ||
        requireAck != newRequireAck ||
        insideSource != newInsideSource || outsideSource != newOutsideSource;
    if (sequence > 0 && number > sequence + 1) missedStates += number - sequence - 1;
    if (pendingAck != newChatId) pendingAck = "";
    if (confirmedAck != newChatId) confirmedAck = "";
    chatId = newChatId;
    requireAck = newRequireAck;
    testMode = simulation;
    insideSource = newInsideSource;
    outsideSource = newOutsideSource;
    sequence = number;
    lastState = now;
    inside = newInside;
    outside = newOutside;
    cue = newCue;
    next = newNext;
  }
}

void network(uint32_t now) {
  if (!configured || baseRole) return;
  if (mode == "udp" && WiFi.status() != WL_CONNECTED) {
    if (udpReady) {
      udp.stop();
      udpReady = false;
      newSession();
    }
    return;
  }
  if (!udpReady) {
    udpReady = mode == "udp" ? udp.begin(0) : radio::ready();
    if (!udpReady) return;
    newSession();
  }
  if (!session.isEmpty() && now - (sequence ? lastState : offeredAt) >= staleAfter) {
    if (sequence) {
      ++snapshotLoss;
      lastStaleAge = now - lastState;
      snapshotLostAt = lastState;
    }
    newSession();
  }
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
         String(StickCP2.Power.getBatteryLevel()) + "|" + String(mode == "udp" ? WiFi.RSSI() : -128) + "|" +
         String(rtt) + "|" + String(now - lastState) + "|" + String(++telemetrySequence));
    if (lastDiagnostic == 0 || now - lastDiagnostic >= 5000) {
      send("TP1|caps|" + deviceId + "|" + session + "|ack");
      send(firmwareMessage(session));
      reportSlots();
      if (mode == "udp") reportConfig();
      send("TP1|diag|" + deviceId + "|" + session + "|" + String(wifiLoss.load()) + "|" +
           String(snapshotLoss) + "|" + String(wifiReason.load()) + "|" + String(lastStaleAge) +
           "|" + String(wifiDowntime.load()) + "|" + String(missedStates) + "|" + String(snapshotDowntime));
      lastDiagnostic = now;
    }
    if (textMode == 0 && !chatId.isEmpty() && shownId == chatId) {
      const char* receipt = pendingAck == chatId ? "ack" : "shown";
      send("TP1|chat_rcpt|" + deviceId + "|" + session + "|" + chatId + "|" + receipt);
    }
  }
  lastSend = now;
}

bool radioMode(const String& value) { return value == "espnow" || value == "espnow-lr" || value == "ble"; }

String slotKey(int slot) { return "slot" + String(slot); }

bool settings(JsonDocument& doc, bool persist, bool apply = true) {
  String host = doc["host"] | "";
  String deviceName = doc["name"] | "";
  String networkName = doc["ssid"] | "";
  String networkPassword = doc["password"] | "";
  String secret = doc["key"] | "";
  String transport = doc["mode"] | "udp";
  String gateway = doc["gateway"] | "";
  int channel = doc["channel"] | 6;
  int slot = doc["slot"] | activeSlot;
  uint8_t remote[6]{};
  IPAddress address;
  uint8_t bytes[32];
  if ((transport == "udp" ? !address.fromString(host.c_str()) || static_cast<uint32_t>(address) == 0 :
       !radioMode(transport) || (transport != "ble" && !radio::parseAddress(gateway, remote))) ||
      channel < 1 || channel > 13 || slot < 0 || slot >= 4 ||
      !unhex(secret, bytes, sizeof(bytes)) || deviceName.isEmpty() || deviceName.length() > 48 ||
      networkName.length() > 32 || networkPassword.length() > 64 ||
      (networkName.isEmpty() && !networkPassword.isEmpty())) return false;
  if (persist) {
    Preferences prefs;
    if (!prefs.begin("tallypilot", false)) return false;
    String savedConfig = prefs.getString("config", "");
    if (savedConfig.length() && !prefs.isKey(slotKey(activeSlot).c_str()) &&
        prefs.putString(slotKey(activeSlot).c_str(), savedConfig) != savedConfig.length()) {
      prefs.end();
      return false;
    }
    // An empty WLAN form keeps the credentials used by the current firmware.
    // This also preserves the pilot's profile when upgrading from 0.2.
    if (networkName.isEmpty()) {
      JsonDocument previous;
      String priorSlot = prefs.getString(slotKey(slot).c_str(), savedConfig);
      if (!deserializeJson(previous, priorSlot)) {
        doc["ssid"] = String(previous["ssid"] | "");
        doc["password"] = String(previous["password"] | "");
      }
    }
    String encoded;
    doc["slot"] = slot;
    serializeJson(doc, encoded);
    bool saved = prefs.putString(slotKey(slot).c_str(), encoded) == encoded.length() &&
        prefs.putString("config", encoded) == encoded.length();
    if (saved && prefs.isKey("base")) saved = prefs.remove("base");
    prefs.end();
    return saved;
  }
  if (!apply) return true;
  memcpy(key, bytes, sizeof(bytes));
  core = address;
  name = deviceName;
  ssid = networkName;
  password = networkPassword;
  mode = transport;
  baseMac = gateway;
  memcpy(gatewayAddress, remote, 6);
  radioChannel = channel;
  activeSlot = slot;
  return true;
}

void reportSlotResult() {
  if (sequence && !slotRequest.isEmpty() && !slotOutcome.isEmpty())
    send("TP1|slot_result|" + deviceId + "|" + session + "|" + slotRequest + "|" + slotOutcome);
}

void reportSlots() {
  Preferences prefs;
  if (!prefs.begin("tallypilot", true)) return;
  String entries;
  for (int slot = 0; slot < 4; ++slot) {
    JsonDocument doc;
    if (deserializeJson(doc, prefs.getString(slotKey(slot).c_str(), "")) ||
        !settings(doc, false, false) || doc["slot"].as<int>() != slot ||
        String(doc["key"] | "") != hex(key, sizeof(key))) continue;
    String target = doc["mode"] | "udp";
    if (entries.length()) entries += ';';
    entries += String(slot) + "," + target;
    if (target == "espnow" || target == "espnow-lr")
      entries += "," + String(doc["gateway"] | "") + "," + String(doc["channel"] | 6);
  }
  prefs.end();
  send("TP1|slots|" + deviceId + "|" + session + "|" + String(activeSlot) + "|" + entries);
  reportSlotResult();
}

void handleSlot(const String& request, const String& slot, uint32_t now) {
  uint8_t token[16];
  if (!unhex(request, token, sizeof(token)) || slot.length() != 1 || slot[0] < '0' || slot[0] > '3') return;
  if (slotRequest == request) { reportSlotResult(); return; }
  if (!trialConfig.isEmpty() || restartAt || wifiTrial || configSwitchAt || chosenSlot >= 0) {
    send("TP1|slot_result|" + deviceId + "|" + session + "|" + request + "|rejected");
    return;
  }
  int selected = slot[0] - '0';
  bool prepared = false;
  Preferences prefs;
  if (selected != activeSlot && prefs.begin("tallypilot", false)) {
    JsonDocument doc;
    String candidate = prefs.getString(slotKey(selected).c_str(), "");
    String marker = request + slot;
    if (!deserializeJson(doc, candidate) && settings(doc, false, false) &&
        doc["slot"].as<int>() == selected && String(doc["key"] | "") == hex(key, sizeof(key)) &&
        prefs.putString("slotRequest", request) == request.length())
      prepared = prefs.putString("slotTrial", marker) == marker.length();
    prefs.end();
  }
  slotRequest = request;
  slotOutcome = prepared ? "testing" : "rejected";
  reportSlotResult();
  if (prepared) restartAt = now + 600;
}

void loadSlotTrial(Preferences& prefs, JsonDocument& current) {
  slotRequest = prefs.getString("slotRequest", "");
  if (slotRequest.isEmpty()) return;
  slotOutcome = String(current["switchRequest"] | "") == slotRequest ? "applied" : "rollback";
  if (!prefs.isKey("slotTrial")) return;
  String marker = prefs.getString("slotTrial", "");
  // Consume the trial before using it. Any reset before commit returns to the last saved profile.
  if (!prefs.remove("slotTrial") || marker.length() != 33 || !marker.startsWith(slotRequest) ||
      marker[32] < '0' || marker[32] > '3') return;
  int selected = marker[32] - '0';
  JsonDocument candidate;
  if (deserializeJson(candidate, prefs.getString(slotKey(selected).c_str(), "")) ||
      candidate["slot"].as<int>() != selected || String(candidate["key"] | "") != hex(key, sizeof(key)) ||
      !settings(candidate, false)) return;
  candidate["switchRequest"] = slotRequest;
  serializeJson(candidate, trialConfig);
  trialDeadline = millis() + 30000;
  slotOutcome = "";
}

void updateSlotTrial(uint32_t now) {
  if (trialConfig.isEmpty()) return;
  if (sequence && !restartAt) {
    Preferences prefs;
    if (prefs.begin("tallypilot", false)) {
      bool committed = prefs.putString("config", trialConfig) == trialConfig.length();
      prefs.end();
      if (committed) {
        trialConfig = "";
        slotOutcome = "applied";
        reportSlots();
        dirty = true;
        return;
      }
    }
    restartAt = now + 100;
  } else if (static_cast<int32_t>(now - trialDeadline) >= 0 && !restartAt) {
    restartAt = now + 100;
  }
}

void connectWifi() {
  if (ssid.isEmpty()) WiFi.begin();
  else {
    esp_wifi_set_storage(WIFI_STORAGE_RAM);
    WiFi.begin(ssid.c_str(), password.c_str());
  }
  password = "";
}

String configFields() {
  String network = WiFi.SSID();
  return String(TALLY_PAGER_VERSION "|") + core.toString() + "|" +
       hex(reinterpret_cast<const uint8_t*>(name.c_str()), name.length()) + "|" +
       hex(reinterpret_cast<const uint8_t*>(network.c_str()), network.length()) + "|" + lastConfigId + "|" + configResult;
}

void reportConfig() {
  if (sequence) send("TP1|cfg_info|" + deviceId + "|" + session + "|" + configFields());
  if (managementReady && !managementSession.isEmpty()) {
    sendUdp(management, managementPc, "TP1|cfg_info|" + deviceId + "|" + managementSession + "|" + configFields());
    lastManagement = millis();
  }
}

void manageWifi(uint32_t now) {
  if (mode != "udp" || !configured || baseRole || !trialConfig.isEmpty() || restartAt) return;
  if (!managementSession.isEmpty() && now - managementTime >= 90000) managementSession = "";
  if (WiFi.status() != WL_CONNECTED) {
    if (managementReady) management.stop();
    managementReady = false;
    return;
  }
  if (!managementReady) managementReady = management.begin(managementPort);
  if (!managementReady) return;
  for (int i = 0; i < 4; ++i) {
    int length = management.parsePacket();
    if (length <= 0) break;
    IPAddress remote = management.remoteIP();
    uint32_t mask = WiFi.subnetMask();
    if (length > maxPacket || !mask || management.remotePort() != port ||
        (static_cast<uint32_t>(remote) & mask) != (static_cast<uint32_t>(WiFi.localIP()) & mask)) {
      management.flush();
      continue;
    }
    char packet[maxPacket + 1];
    if (management.read(packet, length) != length) continue;
    packet[length] = 0;
    String fields[14];
    size_t count = verifyFields(packet, fields);
    uint8_t nonce[16];
    if (count == 4 && fields[1] == "discover" && !wifiTrial && unhex(fields[3], nonce, sizeof(nonce))) {
      if (managementSession.isEmpty() || discoverNonce != fields[3] || managementPc != remote) {
        esp_fill_random(nonce, sizeof(nonce));
        managementSession = hex(nonce, sizeof(nonce));
        managementCounter = 0;
        discoverNonce = fields[3];
        managementPc = remote;
      }
      managementTime = now;
      sendUdp(management, remote, "TP1|found|" + deviceId + "|" + discoverNonce + "|" + managementSession + "|" + configFields());
      sendUdp(management, remote, firmwareMessage(managementSession));
    } else if (count == 9 && fields[1] == "cfg" && !managementSession.isEmpty() &&
        fields[3] == managementSession && managementPc == remote) {
      handleConfig(fields, count, now, managementCounter);
    }
  }
  if (!managementSession.isEmpty() && now - lastManagement >= 2000) {
    sendUdp(management, managementPc, "TP1|cfg_info|" + deviceId + "|" + managementSession + "|" + configFields());
    sendUdp(management, managementPc, firmwareMessage(managementSession));
    lastManagement = now;
  }
}

bool decryptConfig(String* fields, JsonDocument& patch) {
  const String& encrypted = fields[7];
  size_t length = encrypted.length() / 2;
  uint8_t iv[12], auth[16], data[340], plain[340], configKey[32];
  if (!length || length > sizeof(data) || !unhex(fields[6], iv, sizeof(iv)) ||
      !unhex(encrypted, data, length) || !unhex(fields[8], auth, sizeof(auth))) return false;
  const char purpose[] = "TallyPager WLAN config v1";
  mbedtls_md_hmac(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256), key, sizeof(key),
      reinterpret_cast<const uint8_t*>(purpose), sizeof(purpose) - 1, configKey);
  const String associated = deviceId + "|" + fields[3] + "|" + fields[4] + "|" + fields[5];
  mbedtls_gcm_context context;
  mbedtls_gcm_init(&context);
  int result = mbedtls_gcm_setkey(&context, MBEDTLS_CIPHER_ID_AES, configKey, 256);
  if (!result) result = mbedtls_gcm_auth_decrypt(&context, length, iv, sizeof(iv),
      reinterpret_cast<const uint8_t*>(associated.c_str()), associated.length(), auth, sizeof(auth), data, plain);
  mbedtls_gcm_free(&context);
  return !result && !deserializeJson(patch, plain, length) && patch.is<JsonObject>();
}

void finishConfig(const char* result) {
  configResult = result;
  configSwitchAt = configDeadline = 0;
  wifiTrial = false;
  oldSettings.clear();
  proposedSettings.clear();
  reportConfig();
}

void handleConfig(String* fields, size_t count, uint32_t now, uint32_t& lastCounter) {
  uint8_t request[16];
  if (count != 9 || !unhex(fields[4], request, sizeof(request))) return;
  if (fields[4] == lastConfigId) { reportConfig(); return; }
  char* end = nullptr;
  uint32_t counter = strtoul(fields[5].c_str(), &end, 10);
  if (*end || counter <= lastCounter || wifiTrial) return;
  JsonDocument patch;
  if (!decryptConfig(fields, patch)) return;
  lastCounter = counter;
  lastConfigId = fields[4];
  Preferences prefs;
  if (!prefs.begin("tallypilot", true)) { finishConfig("failed"); return; }
  String saved = prefs.getString("config", "");
  prefs.end();
  if (deserializeJson(oldSettings, saved) || deserializeJson(proposedSettings, saved)) {
    finishConfig("failed");
    return;
  }
  // Some older sticks used the Wi-Fi driver's saved credentials.
  if (String(oldSettings["ssid"] | "").isEmpty()) {
    wifi_config_t station{};
    if (esp_wifi_get_config(WIFI_IF_STA, &station) == ESP_OK && station.sta.ssid[0]) {
      char currentSsid[33]{}, currentPassword[65]{};
      memcpy(currentSsid, station.sta.ssid, sizeof(station.sta.ssid));
      memcpy(currentPassword, station.sta.password, sizeof(station.sta.password));
      oldSettings["ssid"] = currentSsid;
      oldSettings["password"] = currentPassword;
    }
  }
  String newName = patch["name"] | "";
  String newHost = patch["host"] | "";
  String newSsid = patch["ssid"] | "";
  String newPassword = patch["password"] | "";
  IPAddress address;
  if (newName.isEmpty() || newName.length() > 48 || !address.fromString(newHost.c_str()) ||
      !static_cast<uint32_t>(address) || newSsid.length() > 32 || newPassword.length() > 64 ||
      (newSsid.isEmpty() && !newPassword.isEmpty())) { finishConfig("failed"); return; }
  proposedSettings["name"] = newName;
  proposedSettings["host"] = newHost;
  if (!newSsid.isEmpty()) {
    proposedSettings["ssid"] = newSsid;
    proposedSettings["password"] = newPassword;
  }
  changeWifi = !newSsid.isEmpty();
  if (address == core && !changeWifi) {
    bool saved = settings(proposedSettings, true);
    if (saved) { name = newName; dirty = true; }
    finishConfig(saved ? "applied" : "failed");
    return;
  }
  configResult = "testing";
  wifiTrial = true;
  configSwitchAt = now + 300;
  configDeadline = now + 30000;
  reportConfig();
}

void updateConfig(uint32_t now) {
  if (!wifiTrial) return;
  auto apply = [](JsonDocument& doc) {
    settings(doc, false);
    udp.stop();
    udpReady = false;
    newSession();
    if (changeWifi) {
      WiFi.disconnect();
      connectWifi();
    } else password = "";
  };
  if (configSwitchAt && static_cast<int32_t>(now - configSwitchAt) >= 0) {
    configSwitchAt = 0;
    apply(proposedSettings);
  } else if (!configSwitchAt && sequence && settings(proposedSettings, true)) {
    finishConfig("applied");
  } else if (static_cast<int32_t>(now - configDeadline) >= 0) {
    apply(oldSettings);
    finishConfig("rollback");
  }
}

bool baseConfig(JsonDocument& doc, bool save) {
  String kind = doc["mode"] | "";
  int channel = doc["channel"] | 6;
  if (!radioMode(kind) || channel < 1 || channel > 13) return false;
  if (save) {
    Preferences prefs;
    if (!prefs.begin("tallypilot", false)) return false;
    String encoded;
    serializeJson(doc, encoded);
    bool success = prefs.putString("base", encoded) == encoded.length();
    prefs.end();
    return success;
  }
  mode = kind;
  radioChannel = channel;
  baseRole = true;
  return true;
}

void status() {
  JsonDocument doc;
  doc["version"] = TALLY_PAGER_VERSION;
  doc["board"] = "m5stick-cplus2";
  JsonArray features = doc["features"].to<JsonArray>();
  for (const char* feature : firmwareFeatures) features.add(feature);
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
  doc["insideSource"] = insideSource;
  doc["outsideSource"] = outsideSource;
  doc["cue"] = cue;
  doc["next"] = next;
  doc["chatId"] = chatId;
  doc["ackPending"] = !pendingAck.isEmpty();
  doc["ackConfirmed"] = !confirmedAck.isEmpty();
  doc["wifiLoss"] = wifiLoss.load();
  doc["wifiReason"] = wifiReason.load();
  doc["snapshotLoss"] = snapshotLoss;
  doc["mode"] = mode;
  doc["baseRole"] = baseRole;
  doc["radioAddress"] = radioMac;
  doc["channel"] = radioChannel;
  doc["radioReady"] = radio::ready();
  doc["slot"] = activeSlot;
  Serial.print("TP ");
  serializeJson(doc, Serial);
  Serial.println();
}

void readUsbSettings() {
  Preferences prefs;
  if (!prefs.begin("tallypilot", true)) { Serial.println("TP ERROR read"); return; }
  String encoded = prefs.getString("config", "");
  prefs.end();
  JsonDocument config, reply;
  if (deserializeJson(config, encoded)) { Serial.println("TP ERROR read"); return; }
  reply["id"] = deviceId;
  for (const char* field : {"name", "host", "ssid", "password", "mode", "gateway"}) {
    const char* value = config[field] | "";
    reply[field] = value;
  }
  if (String(reply["ssid"] | "").isEmpty() && mode == "udp") {
    wifi_config_t station{};
    if (esp_wifi_get_config(WIFI_IF_STA, &station) == ESP_OK && station.sta.ssid[0]) {
      char actualSsid[33]{}, actualPassword[65]{};
      memcpy(actualSsid, station.sta.ssid, sizeof(station.sta.ssid));
      memcpy(actualPassword, station.sta.password, sizeof(station.sta.password));
      reply["ssid"] = String(actualSsid);
      reply["password"] = String(actualPassword);
    }
  }
  reply["channel"] = config["channel"] | 6;
  reply["slot"] = config["slot"] | 0;
  Serial.print("TP SETTINGS ");
  serializeJson(reply, Serial);
  Serial.println();
}

void readBaseView(const char* payload) {
  JsonDocument view;
  if (deserializeJson(view, payload) || !view.is<JsonArray>()) return;
  baseCount = 0;
  for (JsonObject row : view.as<JsonArray>()) {
    if (baseCount == 4) break;
    auto& line = baseLines[baseCount++];
    line.name = String(row["name"] | "").substring(0, 24);
    line.inside = constrain(row["inside"] | 0, 0, 3);
    line.outside = constrain(row["outside"] | 0, 0, 3);
    line.text = String(row["text"] | "").substring(0, 17);
    line.badge = String(row["badge"] | "").substring(0, 16);
    line.online = row["online"] | (line.inside != 0 || line.outside != 0);
    line.insideEnabled = row["insideOn"] | true;
    line.outsideEnabled = row["outsideOn"] | true;
  }
  baseViewAt = millis();
  dirty = true;
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
    if (!serialOverflow && serialLine == "TP READ SETTINGS") readUsbSettings();
    if (!serialOverflow && baseRole && serialLine.startsWith("TP BASEVIEW ")) readBaseView(serialLine.c_str() + 12);
    if (!serialOverflow && serialLine.startsWith("TP CONFIG ")) {
      JsonDocument doc;
      bool ok = !deserializeJson(doc, serialLine.c_str() + 10) && settings(doc, true);
      Serial.println(ok ? "TP OK" : "TP ERROR config");
      if (ok) restartAt = millis() + 350;
    }
    if (!serialOverflow && serialLine.startsWith("TP BASE ")) {
      JsonDocument doc;
      bool ok = !deserializeJson(doc, serialLine.c_str() + 8) && baseConfig(doc, true);
      Serial.println(ok ? "TP OK" : "TP ERROR base");
      if (ok) restartAt = millis() + 350;
    }
    if (!serialOverflow && serialLine == "TP PAGER" && baseRole) {
      Preferences prefs;
      bool ok = false;
      if (prefs.begin("tallypilot", false)) {
        ok = prefs.isKey("config") && prefs.remove("base");
        prefs.end();
      }
      Serial.println(ok ? "TP OK" : "TP ERROR pager");
      if (ok) restartAt = millis() + 350;
    }
    if (!serialOverflow && baseRole && serialLine.startsWith("TP LINK ") && serialLine.length() > 21 &&
        serialLine[20] == ' ') {
      uint8_t address[6];
      if (radio::parseAddress(serialLine.substring(8, 20), address)) radio::send(address, serialLine.substring(21));
    }
    serialLine = "";
    serialOverflow = false;
  }
}

const uint16_t colors[] = {0x4208, 0x1C9F, 0x0640, 0xF904};
const char* states[] = {"?", "SAFE", "PREVIEW", "LIVE"};

int qualityBars() {
  if (mode != "udp" || WiFi.status() != WL_CONNECTED) return -1;
  int rssi = WiFi.RSSI();
  return (rssi >= -82) + (rssi >= -75) + (rssi >= -67) + (rssi >= -55);
}

bool linkDelayed() {
  return sequence && (millis() - lastState >= 750 || rtt >= 250);
}

bool showReconnectNotice() {
  return sequence && millis() - connectedAt < 2200;
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
  if (chosenSlot >= 0) {
    display.fillScreen(TFT_BLACK);
    display.setTextColor(TFT_WHITE);
    display.drawString("Funk- und WLAN-Profil", 5, 12);
    Preferences prefs;
    if (prefs.begin("tallypilot", true)) {
      JsonDocument selected;
      if (!deserializeJson(selected, prefs.getString(slotKey(chosenSlot).c_str(), ""))) {
        display.drawString(String(chosenSlot + 1) + "  " + String(selected["mode"] | "udp"), 5, 42);
        display.drawString(displayText(String(selected["ssid"] | "")).substring(0, 36), 5, 59);
      }
      prefs.end();
    }
    display.drawString("A: Weiter     B: Aktivieren", 5, 94);
  } else if (baseRole) {
    display.fillScreen(TFT_BLACK);
    display.setTextColor(TFT_WHITE);
    display.drawString("Funkbasis   " + mode + (mode == "ble" ? "" : "  K:" + String(radioChannel)), 5, 6);
    if (baseCount && millis() - baseViewAt < 3500) {
      for (size_t i = 0; i < baseCount; ++i) {
        int x = 3 + (i % 2) * 119;
        int y = 20 + (i / 2) * 56;
        auto& row = baseLines[i];
        display.fillRect(x, y, 115, 52, row.outsideEnabled ? colors[row.outside] : TFT_BLACK);
        display.fillRect(x + 3, y + 12, 109, 28, row.insideEnabled ? colors[row.inside] : TFT_BLACK);
        display.drawString(displayText(row.name).substring(0, 17), x + 4, y + 2);
        display.setTextColor(row.insideEnabled && row.inside >= 2 ? TFT_BLACK : TFT_WHITE);
        display.drawString(row.online ? displayText(row.text) : "Getrennt", x + 5, y + 15);
        display.drawString(row.badge, x + 5, y + 27);
        display.setTextColor(TFT_WHITE);
        display.drawString(String("I:") + (row.insideEnabled ? states[row.inside] : "-") +
            " A:" + (row.outsideEnabled ? states[row.outside] : "-"), x + 4, y + 42);
      }
    } else {
      display.drawString("Adresse: " + radioMac, 5, 50);
      display.drawString(radio::ready() ? "Bereit fuer Windows" : "Funkstart fehlgeschlagen", 5, 74);
    }
  } else if (diagnostics) {
    display.fillScreen(TFT_BLACK);
    display.setTextColor(TFT_WHITE);
    display.drawString("TallyPager " TALLY_PAGER_VERSION, 5, 6);
    display.drawString("Stick: " + deviceId, 5, 24);
    display.drawString(mode == "udp" ? "WLAN: " + WiFi.localIP().toString() : "Funk: " + mode, 5, 42);
    display.drawString(mode == "udp" ? "Core: " + core.toString() : "Basis: " + baseMac, 5, 60);
    display.drawString(rtt < 0 ? "Core RTT: --" : "Core RTT: " + String(rtt) + " ms", 5, 78);
    display.drawString("RSSI: " + String(WiFi.RSSI()) + " dBm", 5, 96);
    display.drawString(String("Akku: ") + StickCP2.Power.getBatteryLevel() + "%", 5, 114);
  } else {
    const bool showInside = insideSource != "off";
    const bool showOutside = outsideSource != "off";
    display.fillScreen(showOutside ? colors[outside] : TFT_BLACK);
    display.fillRect(6, 18, 228, 100, showInside ? colors[inside] : TFT_BLACK);
    display.setTextColor(TFT_WHITE);
    String headline = next.isEmpty() ? name : "next: " + displayText(next);
    display.drawString(headline.substring(0, 19), 5, 5);
    const int bars = qualityBars();
    const bool delayed = linkDelayed();
    display.fillRect(127, 0, 113, 18, TFT_BLACK);
    for (int i = 0; mode == "udp" && i < 4; ++i) {
      int height = 3 + 2 * i;
      display.fillRect(128 + i * 5, 14 - height, 3, height, i < bars ? TFT_GREEN : 0x4208);
    }
    display.setTextColor(!sequence ? TFT_LIGHTGREY : delayed ? TFT_ORANGE : TFT_WHITE);
    String connection = !sequence ? "Getrennt" : delayed ? "Delayed" : showReconnectNotice() ? "Verbunden" : "";
    if (rtt >= 0) {
      if (!connection.isEmpty()) connection += " ";
      connection += String(rtt) + "ms";
    }
    display.drawString(connection, mode == "udp" ? 148 : 128, 5);
    display.setTextColor(TFT_WHITE);
    if (beamerLive && sequence) {
      display.fillRect(143, 18, 97, 11, TFT_BLACK);
      display.drawString("LIVE auf BEAMER", 147, 20);
    }
    if (testMode && sequence) {
      display.fillRect(6, 30, 228, 12, TFT_BLACK);
      display.drawString("Simulationssignal vom Rechner", 12, 32);
    }
    if (showOutside) display.drawString(outsideSource + ": " + states[outside], 12, 93);
    if (lastMinute >= 0) {
      char timeText[6];
      snprintf(timeText, sizeof(timeText), "%02d:%02d", lastMinute / 60, lastMinute % 60);
      display.drawString(timeText, 201, 123);
    }
    if (showInside) display.drawString(insideSource + ": " + states[inside], 12, 83);
    if (sequence) {
      display.setTextDatum(top_center);
      display.drawString(displayText(insideLive).substring(0, 36), 120, 105);
      display.drawString(displayText(outsideLive).substring(0, 36), 120, 115);
      display.setTextDatum(top_left);
    }
    if (!configured || sequence == 0) {
      const char* message = !configured ? "USB-Setup starten" :
          mode != "udp" ? (radio::ready() ? "Suche Funkbasis" : "Funkstart fehlgeschlagen") :
          (WiFi.status() == WL_CONNECTED ? "Core offline" : "WLAN nicht verbunden");
      display.drawString(message, 12, 38);
    } else {
      display.setTextColor(showInside && inside >= 2 ? TFT_BLACK : TFT_WHITE);
      int y = testMode || beamerLive ? 46 : 28;
      if (textMode != 0) {
        int tally = textMode == 1 ? (showOutside ? outside : inside) : (showInside ? inside : outside);
        display.fillRect(8, y, 224, 30, TFT_BLACK);
        display.setTextColor(TFT_WHITE);
        display.setTextSize(3);
        display.setTextDatum(top_center);
        display.drawString(states[tally], 120, y + 3);
        display.setTextDatum(top_left);
      } else {
        display.setTextSize(2);
        String text = displayText(cue);
        unsigned int lines = y == 28 ? 3 : 2;
        unsigned int columns = 17, lineHeight = 17;
        if (text.length() > columns * lines) {
          display.setTextSize(1);
          columns = 36;
          lineHeight = 9;
          lines = (80 - y) / lineHeight;
        }
        for (unsigned int i = 0; i < min(text.length(), columns * lines); i += columns)
          display.drawString(text.substring(i, i + columns), 12, y + (i / columns) * lineHeight);
      }
      display.setTextSize(1);
      if (textMode == 0 && requireAck && !chatId.isEmpty()) {
        display.drawString(confirmedAck == chatId ? "Quittiert" :
            pendingAck == chatId ? "Warte auf PC" : "B: quittieren", 12, 125);
      }
      if (textMode == 0 && shownId != chatId) {
        shownId = chatId;
        if (!chatId.isEmpty())
          send("TP1|chat_rcpt|" + deviceId + "|" + session + "|" + chatId + "|shown");
      }
    }
  }
  display.endWrite();
}
}

void setup() {
  Serial.setRxBufferSize(2048);
  Serial.begin(115200);
  StickCP2.begin(M5.config());
  StickCP2.Display.setRotation(1);
  StickCP2.Display.setBrightness(160);
  const uint64_t mac = ESP.getEfuseMac();
  char id[13];
  snprintf(id, sizeof(id), "%012llx", static_cast<unsigned long long>(mac));
  deviceId = id;
  uint8_t macAddress[6];
  esp_read_mac(macAddress, ESP_MAC_WIFI_STA);
  radioMac = radio::formatAddress(macAddress);
  name = "TallyPager";
  Preferences prefs;
  if (prefs.begin("tallypilot", false)) {
    String json = prefs.getString("config", "");
    String base = prefs.getString("base", "");
    JsonDocument doc;
    configured = !deserializeJson(doc, json) && settings(doc, false);
    if (base.length() && !deserializeJson(doc, base)) baseConfig(doc, false);
    if (configured && !baseRole) loadSlotTrial(prefs, doc);
    prefs.end();
  }
  if (baseRole || (configured && mode != "udp")) {
    radio::start(mode, radioChannel, baseRole, gatewayAddress);
  } else if (configured) {
    WiFi.onEvent([](WiFiEvent_t event, WiFiEventInfo_t info) {
      if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED || event == ARDUINO_EVENT_WIFI_STA_LOST_IP) {
        if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) wifiReason = info.wifi_sta_disconnected.reason;
        else if (wifiLostAt.load() == 0) wifiReason = -1;
        if (wifiLostAt.load() == 0) {
          ++wifiLoss;
          wifiLostAt = millis();
        }
      } else if (event == ARDUINO_EVENT_WIFI_STA_GOT_IP) {
        uint32_t lostAt = wifiLostAt.exchange(0);
        if (lostAt != 0) wifiDowntime = millis() - lostAt;
      }
    });
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);
    WiFi.setAutoReconnect(true);
    connectWifi();
  }
  status();
}

bool profileButtons(uint32_t now) {
  if (!configured || baseRole || !trialConfig.isEmpty() || restartAt) return false;
  bool both = M5.BtnA.isPressed() && M5.BtnB.isPressed();
  if (chosenSlot < 0 && both) {
    if (!menuHeld) menuHeld = now;
    if (!menuAttempted && now - menuHeld >= 1800) {
      menuAttempted = true;
      Preferences prefs;
      if (prefs.begin("tallypilot", true)) {
        if (prefs.isKey(slotKey(activeSlot).c_str())) chosenSlot = activeSlot;
        prefs.end();
      }
      menuArmed = false;
      menuTouched = now;
      dirty = true;
    }
    return true;
  }
  menuHeld = 0;
  menuAttempted = false;
  if (chosenSlot < 0) return false;
  if (now - menuTouched >= 15000) {
    chosenSlot = -1;
    dirty = true;
    return true;
  }
  if (!menuArmed) {
    menuArmed = !M5.BtnA.isPressed() && !M5.BtnB.isPressed();
    return true;
  }
  if (M5.BtnA.wasClicked() || M5.BtnB.wasClicked()) {
    Preferences prefs;
    if (prefs.begin("tallypilot", false)) {
      if (M5.BtnB.wasClicked()) {
        String candidate = prefs.getString(slotKey(chosenSlot).c_str(), "");
        JsonDocument doc;
        if (!deserializeJson(doc, candidate) && settings(doc, false, false) &&
            prefs.putString("config", candidate) == candidate.length()) restartAt = now + 150;
        chosenSlot = -1;
      } else {
        for (int offset = 1; offset <= 4; ++offset) {
          int nextSlot = (chosenSlot + offset) % 4;
          if (prefs.isKey(slotKey(nextSlot).c_str())) { chosenSlot = nextSlot; break; }
        }
      }
      prefs.end();
    }
    menuTouched = now;
    dirty = true;
  }
  return true;
}

void loop() {
  M5.update();
  serialInput();
  uint32_t now = millis();
  if (restartAt && static_cast<int32_t>(now - restartAt) >= 0) ESP.restart();
  radio::poll();
  if (baseRole) {
    uint8_t address[6];
    char packet[maxPacket + 1];
    for (int i = 0; i < 8 && radio::read(address, packet); ++i) {
      String frame = String("TP LINK ") + radio::formatAddress(address) + " " + packet + "\n";
      Serial.write(reinterpret_cast<const uint8_t*>(frame.c_str()), frame.length());
    }
  } else {
    network(now);
    updateSlotTrial(now);
    manageWifi(now);
    updateConfig(now);
  }
  bool handlingProfile = profileButtons(now);
  if (presentationAt && now - presentationAt >= 1500) {
    presentationAt = 0;
    textMode = 0;
    beamerLive = false;
    insideLive = outsideLive = "";
    dirty = true;
  }
  if (!handlingProfile && M5.BtnA.wasClicked()) {
    diagnostics = !diagnostics;
    dirty = true;
  }
  if (!handlingProfile && M5.BtnB.wasClicked() && !diagnostics && sequence && textMode == 0 &&
      requireAck && !chatId.isEmpty() && shownId == chatId && confirmedAck != chatId) {
    pendingAck = chatId;
    send("TP1|chat_rcpt|" + deviceId + "|" + session + "|" + chatId + "|ack");
    dirty = true;
  }
  int minute = clockSynced ? (clockSeconds + (now - clockAt) / 1000) % 86400 / 60 : -1;
  if (minute != lastMinute) { lastMinute = minute; dirty = true; }
  if (baseRole && baseCount && now - baseViewAt >= 3500) { baseCount = 0; dirty = true; }
  if (now - lastLinkCheck >= 200) {
    lastLinkCheck = now;
    static int previousBars = -2;
    static bool previousDelay = false;
    static bool previousNotice = false;
    int bars = qualityBars();
    bool delayed = linkDelayed();
    bool notice = showReconnectNotice();
    dirty |= bars != previousBars || delayed != previousDelay || notice != previousNotice;
    previousBars = bars;
    previousDelay = delayed;
    previousNotice = notice;
  }
  if (dirty || (diagnostics && now - lastDraw >= 1000)) {
    draw();
    lastDraw = now;
    dirty = false;
  }
  delay(1);
}
