#include <Arduino.h>
#include <ArduinoOTA.h>
#include <M5StickCPlus2.h>
#include <Preferences.h>
#include <SocketIOclient.h>
#include <WebSocketsClient.h>
#include <WiFi.h>
#include <WiFiManager.h>
#include <WebServer.h>
#include <esp_wifi.h>
#include <ArduinoJson.h>
#include <vector>

#ifndef CAMERA_CUE_VERSION
#define CAMERA_CUE_VERSION "0.1.4-dev"
#endif

namespace {

// Tally Arbiter source type IDs from the current upstream source registry.
constexpr const char* kAtemSourceTypeId = "44b8bc4f";
constexpr const char* kVmixSourceTypeId = "58b6af42";

constexpr uint16_t kProgramColor = 0xF904; // bright red
constexpr uint16_t kPreviewColor = 0x0640; // green
constexpr uint16_t kSafeColor    = 0x1C9F; // blue
constexpr uint16_t kUnknownColor = 0x4208; // dark gray
constexpr uint16_t kBlack        = TFT_BLACK;
constexpr uint16_t kWhite        = TFT_WHITE;

constexpr uint8_t kDisplayRotation = 1; // landscape, 240x135
constexpr uint32_t kMetadataPollMs = 5000;
constexpr uint32_t kRenderHeartbeatMs = 15000;
constexpr uint32_t kSetupHoldMs = 3000;
constexpr uint32_t kFactoryResetHoldMs = 10000;
constexpr uint32_t kWifiReconnectMs = 5000;
constexpr uint32_t kTaReconnectMs = 8000;
constexpr uint32_t kWifiPendingTimeoutMs = 20000;

constexpr int kOuterBandTop = 18;
constexpr int kOuterBandBottom = 17;
constexpr int kInnerX = 7;
constexpr int kInnerY = kOuterBandTop;
constexpr int kInnerW = 226;
constexpr int kInnerH = 100;
constexpr int kInnerPadding = 6;

struct SourceMeta {
  String id;
  String name;
  String typeId;
  bool connected = false;
};

struct DeviceSourceMeta {
  String id;
  String deviceId;
  String sourceId;
  String address;
};

enum class TallyState : uint8_t {
  Unknown = 0,
  Safe,
  Preview,
  Program,
};

Preferences prefs;
WiFiManager wifiManager;
SocketIOclient socket;
WebServer configServer(80);

std::vector<SourceMeta> sources;
std::vector<DeviceSourceMeta> deviceSources;

String deviceId = "null";
String deviceName = "Unassigned";
String listenerName;

String tallyHost = "192.168.178.10";
uint16_t tallyPort = 4455;
String atemSourceHint;
String vmixSourceHint;

String atemDeviceSourceId;
String vmixDeviceSourceId;
String atemSourceName;
String vmixSourceName;
String atemAddress;
String vmixAddress;

bool atemSourceConnected = false;
bool vmixSourceConnected = false;
bool socketConnected = false;
bool showDiagnostics = false;
bool hasConfiguration = false;
bool configApActive = false;
bool configServerStarted = false;
bool buzzerEnabled = true;
bool wifiWasConnected = false;
bool factoryResetPrompt = false;
bool comboConsumed = false;

String programBusId = "program";
String previewBusId = "preview";
String lastDeviceStatesJson;
String cueText;
String nextText;

TallyState atemState = TallyState::Unknown;
TallyState vmixState = TallyState::Unknown;

uint8_t brightnessIndex = 3;
const uint8_t brightnessLevels[] = {40, 70, 110, 160, 220, 255};

unsigned long lastMetadataPoll = 0;
unsigned long lastRender = 0;
unsigned long lastWifiReconnectAttempt = 0;
unsigned long lastTaReconnectAttempt = 0;
unsigned long bothButtonsSince = 0;
unsigned long scheduledRestartAt = 0;
uint32_t wifiReconnectCount = 0;
uint32_t taReconnectCount = 0;
uint32_t socketDisconnectCount = 0;
String lastDisconnectReason = "boot";

WiFiManagerParameter* paramTaHost = nullptr;
WiFiManagerParameter* paramTaPort = nullptr;
WiFiManagerParameter* paramAtemHint = nullptr;
WiFiManagerParameter* paramVmixHint = nullptr;

void configureWifiForReliability() {
  WiFi.setSleep(false);
  // Arduino-ESP32 maps setSleep(false) to no power saving. Keep the IDF call
  // explicit as well so later framework/library changes do not silently re-enable it.
  esp_wifi_set_ps(WIFI_PS_NONE);
}

String asciiFallback(String s) {
  s.replace("ä", "ae"); s.replace("Ä", "Ae");
  s.replace("ö", "oe"); s.replace("Ö", "Oe");
  s.replace("ü", "ue"); s.replace("Ü", "Ue");
  s.replace("ß", "ss");
  return s;
}

const char* stateShort(TallyState state) {
  switch (state) {
    case TallyState::Program: return "LIVE";
    case TallyState::Preview: return "PREVIEW";
    case TallyState::Safe: return "SAFE";
    default: return "?";
  }
}

uint16_t stateColor(TallyState state) {
  switch (state) {
    case TallyState::Program: return kProgramColor;
    case TallyState::Preview: return kPreviewColor;
    case TallyState::Safe: return kSafeColor;
    default: return kUnknownColor;
  }
}

uint16_t stateTextColor(TallyState state) {
  return state == TallyState::Program || state == TallyState::Preview ? kBlack : kWhite;
}

TallyState promoteState(TallyState current, const String& busType) {
  if (busType == "program") return TallyState::Program;
  if (busType == "preview" && current != TallyState::Program) return TallyState::Preview;
  return current;
}

String busTypeForId(const String& busId) {
  if (busId == programBusId) return "program";
  if (busId == previewBusId) return "preview";
  return "other";
}

SourceMeta* sourceById(const String& id) {
  for (auto& source : sources) {
    if (source.id == id) return &source;
  }
  return nullptr;
}

bool hintMatches(const String& sourceName, const String& hint) {
  if (hint.isEmpty()) return true;
  String a = sourceName;
  String b = hint;
  a.toLowerCase();
  b.toLowerCase();
  return a.indexOf(b) >= 0;
}

void render();
void processLastDeviceStates();

void resolveRoleMappings() {
  String oldAtem = atemDeviceSourceId;
  String oldVmix = vmixDeviceSourceId;

  atemDeviceSourceId = "";
  vmixDeviceSourceId = "";
  atemSourceName = "";
  vmixSourceName = "";
  atemAddress = "";
  vmixAddress = "";
  atemSourceConnected = false;
  vmixSourceConnected = false;

  // First pass honors optional source-name hints.
  for (const auto& ds : deviceSources) {
    if (ds.deviceId != deviceId) continue;
    SourceMeta* src = sourceById(ds.sourceId);
    if (!src) continue;

    if (src->typeId == kAtemSourceTypeId && atemDeviceSourceId.isEmpty() && hintMatches(src->name, atemSourceHint)) {
      atemDeviceSourceId = ds.id;
      atemSourceName = src->name;
      atemAddress = ds.address;
      atemSourceConnected = src->connected;
    }
    if (src->typeId == kVmixSourceTypeId && vmixDeviceSourceId.isEmpty() && hintMatches(src->name, vmixSourceHint)) {
      vmixDeviceSourceId = ds.id;
      vmixSourceName = src->name;
      vmixAddress = ds.address;
      vmixSourceConnected = src->connected;
    }
  }

  // Fallback to the first source of the correct type if a hint did not match.
  for (const auto& ds : deviceSources) {
    if (ds.deviceId != deviceId) continue;
    SourceMeta* src = sourceById(ds.sourceId);
    if (!src) continue;

    if (src->typeId == kAtemSourceTypeId && atemDeviceSourceId.isEmpty()) {
      atemDeviceSourceId = ds.id;
      atemSourceName = src->name;
      atemAddress = ds.address;
      atemSourceConnected = src->connected;
    }
    if (src->typeId == kVmixSourceTypeId && vmixDeviceSourceId.isEmpty()) {
      vmixDeviceSourceId = ds.id;
      vmixSourceName = src->name;
      vmixAddress = ds.address;
      vmixSourceConnected = src->connected;
    }
  }

  if (oldAtem != atemDeviceSourceId || oldVmix != vmixDeviceSourceId) {
    Serial.printf("Role mapping: ATEM='%s' vMix='%s'\n", atemDeviceSourceId.c_str(), vmixDeviceSourceId.c_str());
  }

  processLastDeviceStates();
}

void parseSources(JsonArray array) {
  sources.clear();
  for (JsonObject obj : array) {
    SourceMeta meta;
    meta.id = String(obj["id"] | "");
    meta.name = String(obj["name"] | "");
    meta.typeId = String(obj["sourceTypeId"] | "");
    meta.connected = obj["connected"] | false;
    sources.push_back(meta);
  }
  resolveRoleMappings();
}

void parseDeviceSources(JsonArray array) {
  deviceSources.clear();
  for (JsonObject obj : array) {
    DeviceSourceMeta meta;
    meta.id = String(obj["id"] | "");
    meta.deviceId = String(obj["deviceId"] | "");
    meta.sourceId = String(obj["sourceId"] | "");
    meta.address = String(obj["address"] | "");
    deviceSources.push_back(meta);
  }
  resolveRoleMappings();
}

void parseBusOptions(JsonArray array) {
  for (JsonObject obj : array) {
    String type = String(obj["type"] | "");
    String id = String(obj["id"] | "");
    if (type == "program") programBusId = id;
    if (type == "preview") previewBusId = id;
  }
  processLastDeviceStates();
}

void processDeviceStates(JsonArray states) {
  TallyState newAtem = (!socketConnected || atemDeviceSourceId.isEmpty() || !atemSourceConnected)
                           ? TallyState::Unknown
                           : TallyState::Safe;
  TallyState newVmix = (!socketConnected || vmixDeviceSourceId.isEmpty() || !vmixSourceConnected)
                           ? TallyState::Unknown
                           : TallyState::Safe;

  for (JsonObject state : states) {
    String busId = String(state["busId"] | "");
    String busType = busTypeForId(busId);
    if (busType != "program" && busType != "preview") continue;

    JsonArray activeSources = state["sources"].as<JsonArray>();
    for (JsonVariant value : activeSources) {
      String activeDeviceSourceId = value.as<String>();
      if (activeDeviceSourceId == atemDeviceSourceId) {
        newAtem = promoteState(newAtem, busType);
      }
      if (activeDeviceSourceId == vmixDeviceSourceId) {
        newVmix = promoteState(newVmix, busType);
      }
    }
  }

  bool changed = newAtem != atemState || newVmix != vmixState;
  atemState = newAtem;
  vmixState = newVmix;
  if (changed) render();
}

void processLastDeviceStates() {
  if (lastDeviceStatesJson.isEmpty()) {
    atemState = TallyState::Unknown;
    vmixState = TallyState::Unknown;
    render();
    return;
  }

  JsonDocument doc;
  if (deserializeJson(doc, lastDeviceStatesJson)) return;
  processDeviceStates(doc.as<JsonArray>());
}

void parseDevices(JsonArray array) {
  for (JsonObject obj : array) {
    if (String(obj["id"] | "") == deviceId) {
      String newName = String(obj["name"] | "Unassigned");
      if (newName != deviceName) {
        deviceName = newName;
        prefs.begin("cameracue", false);
        prefs.putString("devname", deviceName);
        prefs.end();
        render();
      }
      return;
    }
  }
}

void saveAssignedDevice(const String& newId) {
  if (newId.isEmpty() || newId == deviceId) return;
  Serial.println("Saving Tally Arbiter device assignment: " + newId);
  deviceId = newId;
  prefs.begin("cameracue", false);
  prefs.putString("deviceid", deviceId);
  prefs.end();
}

bool isTallyArbiterSystemNotice(const String& message) {
  String m = message;
  m.trim();
  return m.startsWith("Listener Client Connected:") ||
         m.startsWith("Listener Client Disconnected:") ||
         m.startsWith("Test Mode Enabled") ||
         m.startsWith("Test Mode Disabled") ||
         m.startsWith("Source Connected:") ||
         m.startsWith("Source Disconnected:");
}

void applyMessage(String message, const String& messageType = "") {
  message.trim();
  if (message.isEmpty()) return;

  // Tally Arbiter marks its own operational notices with type="server".
  // Those messages (connect/disconnect, test mode, source notices, ...) are
  // diagnostics and must never replace a camera operator cue. Keep the text
  // fallback for compatibility with older servers/clients.
  String normalizedType = messageType;
  normalizedType.toLowerCase();
  if (normalizedType == "server" || isTallyArbiterSystemNotice(message)) {
    Serial.println("TA system notice: " + message);
    return;
  }

  if (message.startsWith("[NEXT]")) {
    nextText = message.substring(6);
    nextText.trim();
  } else if (message == "[NEXT:CLEAR]") {
    nextText = "";
  } else if (message.startsWith("[CUE]")) {
    cueText = message.substring(5);
    cueText.trim();
    if (buzzerEnabled) StickCP2.Speaker.tone(3500, 80);
  } else if (message == "[CLEAR]") {
    cueText = "";
  } else if (message == "[CLEARALL]") {
    cueText = "";
    nextText = "";
  } else {
    // Preserve compatibility with normal Producer messages.
    cueText = message;
    if (buzzerEnabled) StickCP2.Speaker.tone(3500, 80);
  }
  render();
}

void sendEventNoArgs(const char* eventName) {
  JsonDocument doc;
  JsonArray root = doc.to<JsonArray>();
  root.add(eventName);
  String payload;
  serializeJson(doc, payload);
  socket.sendEVENT(payload);
}

void sendEventStringArg(const char* eventName, const String& value) {
  JsonDocument doc;
  JsonArray root = doc.to<JsonArray>();
  root.add(eventName);
  root.add(value);
  String payload;
  serializeJson(doc, payload);
  socket.sendEVENT(payload);
}

void sendListenerHello() {
  JsonDocument doc;
  JsonArray root = doc.to<JsonArray>();
  root.add("listenerclient_connect");
  JsonObject obj = root.add<JsonObject>();
  obj["deviceId"] = deviceId;
  obj["internalId"] = listenerName;
  obj["listenerType"] = String("CameraCue M5StickC Plus2 v") + CAMERA_CUE_VERSION;
  obj["canBeReassigned"] = true;
  obj["canBeFlashed"] = true;
  obj["supportsChat"] = true;

  String payload;
  serializeJson(doc, payload);
  socket.sendEVENT(payload);
}

void requestMetadata() {
  if (!socketConnected) return;
  sendEventNoArgs("sources");
  sendEventNoArgs("device_sources");
  sendEventNoArgs("devices");
  sendEventNoArgs("bus_options");
  // Re-request the authoritative state as well. This makes CameraCue recover
  // even if a transient WiFi interruption caused a pushed state event to be missed.
  if (!deviceId.isEmpty() && deviceId != "null") {
    sendEventStringArg("device_states", deviceId);
  }
}

void flashAttention() {
  for (int i = 0; i < 3; ++i) {
    StickCP2.Display.fillScreen(TFT_WHITE);
    delay(120);
    StickCP2.Display.fillScreen(TFT_BLACK);
    delay(100);
  }
  render();
}

void onSocketEvent(socketIOmessageType_t type, uint8_t* payload, size_t length) {
  switch (type) {
    case sIOtype_CONNECT:
      socketConnected = true;
      lastTaReconnectAttempt = millis();
      Serial.println("Socket.IO connected to Tally Arbiter");
      sendListenerHello();
      requestMetadata();
      render();
      break;

    case sIOtype_DISCONNECT:
      socketConnected = false;
      atemState = TallyState::Unknown;
      vmixState = TallyState::Unknown;
      ++socketDisconnectCount;
      if (WiFi.status() == WL_CONNECTED) lastDisconnectReason = "TA socket lost";
      Serial.println("Socket.IO disconnected");
      render();
      break;

    case sIOtype_ERROR:
      if (WiFi.status() == WL_CONNECTED) lastDisconnectReason = "TA socket error";
      Serial.print("Socket.IO error");
      if (payload != nullptr && length > 0) {
        Serial.print(": ");
        Serial.write(payload, length);
      }
      Serial.println();
      render();
      break;

    case sIOtype_EVENT: {
      JsonDocument doc;
      DeserializationError err = deserializeJson(doc, payload, length);
      if (err) {
        Serial.printf("Socket event JSON error: %s\n", err.c_str());
        return;
      }

      JsonArray root = doc.as<JsonArray>();
      if (root.size() < 1) return;
      String eventName = root[0].as<String>();

      if (eventName == "bus_options" && root.size() >= 2) {
        parseBusOptions(root[1].as<JsonArray>());
      } else if (eventName == "sources" && root.size() >= 2) {
        parseSources(root[1].as<JsonArray>());
      } else if (eventName == "device_sources" && root.size() >= 2) {
        parseDeviceSources(root[1].as<JsonArray>());
      } else if (eventName == "devices" && root.size() >= 2) {
        parseDevices(root[1].as<JsonArray>());
      } else if (eventName == "device_states" && root.size() >= 2) {
        lastDeviceStatesJson = "";
        serializeJson(root[1], lastDeviceStatesJson);
        processDeviceStates(root[1].as<JsonArray>());
      } else if (eventName == "deviceId" && root.size() >= 2) {
        saveAssignedDevice(root[1].as<String>());
        requestMetadata();
      } else if (eventName == "reassign" && root.size() >= 3) {
        String newId = root[2].as<String>();
        saveAssignedDevice(newId);
        StickCP2.Display.fillScreen(TFT_YELLOW);
        StickCP2.Display.setTextColor(TFT_BLACK, TFT_YELLOW);
        StickCP2.Display.setTextDatum(middle_center);
        StickCP2.Display.drawString("Reassigned - restarting", 120, 67);
        delay(700);
        ESP.restart();
      } else if (eventName == "flash") {
        flashAttention();
      } else if ((eventName == "messaging" || eventName == "messaging_client") && root.size() >= 2) {
        // Current TA shape: [event, type, socketId, message]. Older variants may
        // have fewer arguments, therefore the message is always read from the end.
        String messageType = root.size() >= 3 ? root[1].as<String>() : String("");
        String message = root[root.size() - 1].as<String>();
        applyMessage(message, messageType);
      }
      break;
    }

    default:
      break;
  }
}

void drawWrappedText(const String& rawText, int x, int y, int width, int height) {
  String text = asciiFallback(rawText);
  if (text.isEmpty()) return;

  // Try larger text first, then fall back if it would need too many lines.
  uint8_t textSize = 2;
  for (int pass = 0; pass < 2; ++pass) {
    StickCP2.Display.setTextSize(textSize);
    const int lineHeight = textSize == 2 ? 18 : 10;
    const int maxLines = height / lineHeight;

    std::vector<String> lines;
    String current;
    int start = 0;
    while (start < static_cast<int>(text.length())) {
      int space = text.indexOf(' ', start);
      String word;
      if (space < 0) {
        word = text.substring(start);
        start = text.length();
      } else {
        word = text.substring(start, space);
        start = space + 1;
      }
      if (word.isEmpty()) continue;

      String candidate = current.isEmpty() ? word : current + " " + word;
      if (StickCP2.Display.textWidth(candidate) <= width) {
        current = candidate;
      } else {
        if (!current.isEmpty()) lines.push_back(current);
        current = word;
      }
    }
    if (!current.isEmpty()) lines.push_back(current);

    if (static_cast<int>(lines.size()) <= maxLines || textSize == 1) {
      StickCP2.Display.setTextDatum(top_left);
      for (int i = 0; i < static_cast<int>(lines.size()) && i < maxLines; ++i) {
        String line = lines[i];
        if (i == maxLines - 1 && static_cast<int>(lines.size()) > maxLines) line += "...";
        StickCP2.Display.drawString(line, x, y + i * lineHeight);
      }
      return;
    }
    textSize = 1;
  }
}

void renderMain() {
  const uint16_t outer = stateColor(vmixState);
  const uint16_t inner = stateColor(atemState);
  const uint16_t outerText = stateTextColor(vmixState);
  const uint16_t innerText = stateTextColor(atemState);

  StickCP2.Display.startWrite();
  StickCP2.Display.fillScreen(outer);

  // Inner ATEM region.
  StickCP2.Display.fillRect(kInnerX, kInnerY, kInnerW, kInnerH, inner);
  StickCP2.Display.drawRect(kInnerX, kInnerY, kInnerW, kInnerH, TFT_BLACK);

  // Header / "next" field.
  StickCP2.Display.setTextSize(1);
  StickCP2.Display.setTextDatum(top_left);
  StickCP2.Display.setTextColor(outerText, outer);
  String header = nextText.isEmpty() ? deviceName : String("next: ") + asciiFallback(nextText);
  if (StickCP2.Display.textWidth(header) > 190) {
    while (header.length() > 3 && StickCP2.Display.textWidth(header + "...") > 190) {
      header.remove(header.length() - 1);
    }
    header += "...";
  }
  StickCP2.Display.drawString(header, 3, 3);

  // Small battery indicator, intentionally textual in v0.1.
  int battery = StickCP2.Power.getBatteryLevel();
  String batteryText = battery >= 0 ? String(battery) + "%" : String("BAT?");
  StickCP2.Display.setTextDatum(top_right);
  StickCP2.Display.drawString(batteryText, 237, 3);

  // Cue text.
  StickCP2.Display.setTextColor(innerText, inner);
  drawWrappedText(cueText, kInnerX + kInnerPadding, kInnerY + 8,
                  kInnerW - 2 * kInnerPadding, kInnerH - 30);

  // Inner camera/ATEM status.
  StickCP2.Display.setTextSize(1);
  StickCP2.Display.setTextDatum(bottom_right);
  String innerStatus = String("Kamera in ATEM ") + stateShort(atemState);
  StickCP2.Display.drawString(innerStatus, kInnerX + kInnerW - 4, kInnerY + kInnerH - 4);

  // Outer vMix status.
  StickCP2.Display.setTextColor(outerText, outer);
  String outerStatus = String("Mixer in vMix ") + stateShort(vmixState);
  StickCP2.Display.drawString(outerStatus, 237, 132);

  // Network problems must be distinguishable and can never look like SAFE.
  if (WiFi.status() != WL_CONNECTED) {
    StickCP2.Display.setTextDatum(top_center);
    StickCP2.Display.setTextColor(TFT_WHITE, TFT_RED);
    StickCP2.Display.fillRect(80, 0, 80, 13, TFT_RED);
    StickCP2.Display.drawString("WIFI OFF", 120, 2);
  } else if (!socketConnected) {
    StickCP2.Display.setTextDatum(top_center);
    StickCP2.Display.setTextColor(TFT_WHITE, TFT_RED);
    StickCP2.Display.fillRect(80, 0, 80, 13, TFT_RED);
    StickCP2.Display.drawString("TA OFFLINE", 120, 2);
  }

  StickCP2.Display.endWrite();
}

void renderDiagnostics() {
  StickCP2.Display.startWrite();
  StickCP2.Display.fillScreen(TFT_BLACK);
  StickCP2.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  StickCP2.Display.setTextSize(1);
  StickCP2.Display.setTextDatum(top_left);

  int y = 2;
  auto line = [&](const String& value, uint16_t color = TFT_WHITE) {
    StickCP2.Display.setTextColor(color, TFT_BLACK);
    StickCP2.Display.drawString(asciiFallback(value), 3, y);
    y += 11;
  };

  line(String("CameraCue v") + CAMERA_CUE_VERSION, TFT_CYAN);
  line("Dev: " + deviceName);
  if (WiFi.status() == WL_CONNECTED) {
    line("WiFi: " + WiFi.localIP().toString() + " " + String(WiFi.RSSI()) + "dBm");
    line("Setup: http://" + WiFi.localIP().toString());
  } else {
    line("WiFi: OFF", TFT_RED);
    if (configApActive) line("Setup AP: 192.168.4.1", TFT_CYAN);
  }
  line(String("TA: ") + tallyHost + ":" + String(tallyPort) + (socketConnected ? " OK" : " OFF"), socketConnected ? TFT_GREEN : TFT_RED);

  String atemLine = "ATEM: ";
  if (atemDeviceSourceId.isEmpty()) atemLine += "NO MAP";
  else atemLine += atemSourceName + " @" + atemAddress + " " + stateShort(atemState);
  line(atemLine, atemState == TallyState::Unknown ? TFT_YELLOW : TFT_WHITE);

  String vmixLine = "vMix: ";
  if (vmixDeviceSourceId.isEmpty()) vmixLine += "NO MAP";
  else vmixLine += vmixSourceName + " @" + vmixAddress + " " + stateShort(vmixState);
  line(vmixLine, vmixState == TallyState::Unknown ? TFT_YELLOW : TFT_WHITE);

  line("RC W:" + String(wifiReconnectCount) + " TA:" + String(taReconnectCount) + " D:" + String(socketDisconnectCount), TFT_DARKGREY);
  line("Last: " + lastDisconnectReason, TFT_DARKGREY);
  line("A+B 3s: Setup  10s: Reset", TFT_DARKGREY);
  StickCP2.Display.endWrite();
}

void render() {
  if (showDiagnostics) renderDiagnostics();
  else renderMain();
  lastRender = millis();
}

void cycleBrightness() {
  brightnessIndex = (brightnessIndex + 1) % (sizeof(brightnessLevels) / sizeof(brightnessLevels[0]));
  StickCP2.Display.setBrightness(brightnessLevels[brightnessIndex]);
  prefs.begin("cameracue", false);
  prefs.putUChar("bright", brightnessIndex);
  prefs.end();
  render();
}

String htmlEscape(String value) {
  value.replace("&", "&amp;");
  value.replace("<", "&lt;");
  value.replace(">", "&gt;");
  value.replace("\"", "&quot;");
  return value;
}

String storedWifiSsid() {
  wifi_config_t cfg{};
  if (esp_wifi_get_config(WIFI_IF_STA, &cfg) != ESP_OK) return "";
  return String(reinterpret_cast<const char*>(cfg.sta.ssid));
}

String storedWifiPassword() {
  wifi_config_t cfg{};
  if (esp_wifi_get_config(WIFI_IF_STA, &cfg) != ESP_OK) return "";
  return String(reinterpret_cast<const char*>(cfg.sta.password));
}

void clearPendingWifi() {
  prefs.begin("cameracue", false);
  prefs.putBool("wifiPending", false);
  prefs.remove("newSsid");
  prefs.remove("newPass");
  prefs.remove("oldSsid");
  prefs.remove("oldPass");
  prefs.end();
}

bool waitForWifi(uint32_t timeoutMs) {
  const unsigned long started = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - started < timeoutMs) {
    M5.update();
    delay(50);
  }
  return WiFi.status() == WL_CONNECTED;
}

bool applyPendingWifiIfNeeded() {
  prefs.begin("cameracue", true);
  bool pending = prefs.getBool("wifiPending", false);
  String newSsid = prefs.getString("newSsid", "");
  String newPass = prefs.getString("newPass", "");
  String oldSsid = prefs.getString("oldSsid", "");
  String oldPass = prefs.getString("oldPass", "");
  prefs.end();
  if (!pending || newSsid.isEmpty()) return false;

  StickCP2.Display.fillScreen(TFT_BLACK);
  StickCP2.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  StickCP2.Display.setTextDatum(top_left);
  StickCP2.Display.drawString("Teste neues WLAN...", 5, 10);
  StickCP2.Display.drawString(asciiFallback(newSsid), 5, 28);

  WiFi.mode(WIFI_STA);
  configureWifiForReliability();
  WiFi.disconnect(false, false);
  delay(100);
  WiFi.begin(newSsid.c_str(), newPass.c_str());
  if (waitForWifi(kWifiPendingTimeoutMs)) {
    Serial.println("New WiFi credentials validated.");
    clearPendingWifi();
    lastDisconnectReason = "WiFi changed OK";
    return true;
  }

  Serial.println("New WiFi failed; restoring previous credentials.");
  WiFi.disconnect(false, false);
  delay(100);
  if (!oldSsid.isEmpty()) {
    WiFi.begin(oldSsid.c_str(), oldPass.c_str());
    if (waitForWifi(kWifiPendingTimeoutMs)) {
      clearPendingWifi();
      lastDisconnectReason = "WiFi rollback";
      return true;
    }
  }
  clearPendingWifi();
  return false;
}

void factoryResetConfiguration() {
  StickCP2.Display.fillScreen(TFT_YELLOW);
  StickCP2.Display.setTextColor(TFT_BLACK, TFT_YELLOW);
  StickCP2.Display.setTextDatum(middle_center);
  StickCP2.Display.setTextSize(1);
  StickCP2.Display.drawString("FACTORY RESET", 120, 52);
  StickCP2.Display.drawString("WiFi + CameraCue werden geloescht", 120, 70);

  configServer.stop();
  WiFi.softAPdisconnect(true);
  wifiManager.resetSettings();
  prefs.begin("cameracue", false);
  prefs.clear();
  prefs.end();
  delay(800);
  ESP.restart();
}

void loadPreferences() {
  prefs.begin("cameracue", true);
  deviceId = prefs.getString("deviceid", "null");
  deviceName = prefs.getString("devname", "Unassigned");
  tallyHost = prefs.getString("taHost", tallyHost);
  tallyPort = prefs.getUShort("taPort", 4455);
  atemSourceHint = prefs.getString("atemHint", "");
  vmixSourceHint = prefs.getString("vmixHint", "");
  brightnessIndex = prefs.getUChar("bright", brightnessIndex);
  buzzerEnabled = prefs.getBool("buzzer", true);
  hasConfiguration = prefs.getBool("configured", false);
  prefs.end();
  if (brightnessIndex >= sizeof(brightnessLevels) / sizeof(brightnessLevels[0])) brightnessIndex = 3;
}

void savePortalParams() {
  String host = paramTaHost ? String(paramTaHost->getValue()) : tallyHost;
  String portString = paramTaPort ? String(paramTaPort->getValue()) : String(tallyPort);
  String aHint = paramAtemHint ? String(paramAtemHint->getValue()) : atemSourceHint;
  String vHint = paramVmixHint ? String(paramVmixHint->getValue()) : vmixSourceHint;

  host.trim(); portString.trim(); aHint.trim(); vHint.trim();
  if (!host.isEmpty()) tallyHost = host;
  uint16_t port = static_cast<uint16_t>(portString.toInt());
  if (port > 0) tallyPort = port;
  atemSourceHint = aHint;
  vmixSourceHint = vHint;

  prefs.begin("cameracue", false);
  prefs.putString("taHost", tallyHost);
  prefs.putUShort("taPort", tallyPort);
  prefs.putString("atemHint", atemSourceHint);
  prefs.putString("vmixHint", vmixSourceHint);
  prefs.putBool("buzzer", buzzerEnabled);
  prefs.putBool("configured", true);
  prefs.end();
  hasConfiguration = true;
}

String settingsPage(const String& notice = "") {
  String ssid = WiFi.status() == WL_CONNECTED ? WiFi.SSID() : storedWifiSsid();
  String ip = WiFi.status() == WL_CONNECTED ? WiFi.localIP().toString() : WiFi.softAPIP().toString();
  String page;
  page.reserve(6500);
  page += F("<!doctype html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'>");
  page += F("<title>CameraCue Settings</title><style>body{font-family:system-ui;background:#111;color:#eee;max-width:720px;margin:30px auto;padding:0 16px}fieldset{border:1px solid #444;margin:18px 0;padding:16px}label{display:block;margin:10px 0}input,select{width:100%;box-sizing:border-box;padding:9px;background:#222;color:#fff;border:1px solid #555}button{padding:10px 16px;margin:8px 8px 8px 0}small{color:#aaa}.ok{background:#173d24;padding:10px}.warn{background:#513d12;padding:10px}</style></head><body>");
  page += "<h1>CameraCue " + String(CAMERA_CUE_VERSION) + "</h1>";
  if (!notice.isEmpty()) page += "<p class='ok'>" + htmlEscape(notice) + "</p>";
  page += "<p><b>Device:</b> " + htmlEscape(deviceName) + "<br><b>IP:</b> " + htmlEscape(ip) + "<br><b>Tally Arbiter:</b> " + (socketConnected ? "verbunden" : "offline") + "</p>";
  page += F("<form method='post' action='/save'><fieldset><legend>WLAN</legend>");
  page += "<label>SSID<input name='ssid' value='" + htmlEscape(ssid) + "'></label>";
  page += F("<label>Neues Passwort<input name='wifiPass' type='password' placeholder='leer = bisheriges Passwort behalten'></label><label><input style='width:auto' type='checkbox' name='wifiOpen' value='1'> Offenes WLAN (kein Passwort)</label><small>Bestehendes Passwort bleibt bei leerem Feld erhalten. Fuer ein offenes WLAN die Checkbox aktivieren. Neue Daten werden getestet und bei Fehlschlag zurueckgerollt.</small></fieldset>");
  page += F("<fieldset><legend>Tally Arbiter</legend>");
  page += "<label>Server / IP<input name='taHost' value='" + htmlEscape(tallyHost) + "'></label>";
  page += "<label>Port<input name='taPort' type='number' min='1' max='65535' value='" + String(tallyPort) + "'></label>";
  page += "<label>ATEM Source Hint (optional)<input name='atemHint' value='" + htmlEscape(atemSourceHint) + "'></label>";
  page += "<label>vMix Source Hint (optional)<input name='vmixHint' value='" + htmlEscape(vmixSourceHint) + "'></label></fieldset>";
  page += F("<fieldset><legend>Geraet</legend><label>Helligkeit<select name='bright'>");
  for (size_t i=0; i<sizeof(brightnessLevels)/sizeof(brightnessLevels[0]); ++i) {
    page += "<option value='" + String(i) + "'" + (i == brightnessIndex ? " selected" : "") + ">" + String(brightnessLevels[i]) + "</option>";
  }
  page += F("</select></label><label><input style='width:auto' type='checkbox' name='buzzer' value='1'");
  if (buzzerEnabled) page += F(" checked");
  page += F("> Buzzer bei neuer Regieanweisung</label></fieldset><button type='submit'>Speichern & neu verbinden</button></form>");
  page += F("<form method='post' action='/reboot'><button>Neustart</button></form>");
  page += F("<p><small>Setup-AP: A+B 3 s halten. Factory Reset: A+B 10 s halten und danach bestaetigen.</small></p></body></html>");
  return page;
}

void scheduleRestart(uint32_t delayMs = 1200) {
  scheduledRestartAt = millis() + delayMs;
}

void handleSettingsSave() {
  String newHost = configServer.arg("taHost"); newHost.trim();
  int newPort = configServer.arg("taPort").toInt();
  if (!newHost.isEmpty()) tallyHost = newHost;
  if (newPort > 0 && newPort <= 65535) tallyPort = static_cast<uint16_t>(newPort);
  atemSourceHint = configServer.arg("atemHint"); atemSourceHint.trim();
  vmixSourceHint = configServer.arg("vmixHint"); vmixSourceHint.trim();
  int newBrightness = configServer.arg("bright").toInt();
  if (newBrightness >= 0 && newBrightness < static_cast<int>(sizeof(brightnessLevels)/sizeof(brightnessLevels[0]))) brightnessIndex = newBrightness;
  buzzerEnabled = configServer.hasArg("buzzer");

  String requestedSsid = configServer.arg("ssid"); requestedSsid.trim();
  String requestedPass = configServer.arg("wifiPass");
  const bool openNetwork = configServer.hasArg("wifiOpen");
  String currentSsid = WiFi.status() == WL_CONNECTED ? WiFi.SSID() : storedWifiSsid();
  String currentPass = storedWifiPassword();
  if (!openNetwork && requestedPass.isEmpty()) requestedPass = currentPass;
  if (openNetwork) requestedPass = "";
  bool wifiChange = !requestedSsid.isEmpty() && (requestedSsid != currentSsid || openNetwork || configServer.arg("wifiPass").length() > 0);
  if (wifiChange) {
    prefs.begin("cameracue", false);
    prefs.putBool("wifiPending", true);
    prefs.putString("newSsid", requestedSsid);
    prefs.putString("newPass", requestedPass);
    prefs.putString("oldSsid", currentSsid);
    prefs.putString("oldPass", currentPass);
    prefs.end();
  }

  prefs.begin("cameracue", false);
  prefs.putString("taHost", tallyHost);
  prefs.putUShort("taPort", tallyPort);
  prefs.putString("atemHint", atemSourceHint);
  prefs.putString("vmixHint", vmixSourceHint);
  prefs.putUChar("bright", brightnessIndex);
  prefs.putBool("buzzer", buzzerEnabled);
  prefs.putBool("configured", true);
  prefs.end();
  hasConfiguration = true;

  configServer.send(200, "text/html; charset=utf-8", settingsPage(wifiChange ? "Gespeichert. WLAN wird sicher getestet; das Geraet startet neu." : "Gespeichert. Das Geraet startet neu."));
  scheduleRestart(1800);
}

void startConfigWebServer() {
  if (configServerStarted) return;
  configServer.on("/", HTTP_GET, [](){ configServer.send(200, "text/html; charset=utf-8", settingsPage()); });
  configServer.on("/settings", HTTP_GET, [](){ configServer.sendHeader("Location", "/"); configServer.send(302); });
  configServer.on("/save", HTTP_POST, handleSettingsSave);
  configServer.on("/status", HTTP_GET, [](){
    JsonDocument doc;
    doc["version"] = CAMERA_CUE_VERSION;
    doc["device"] = deviceName;
    doc["wifi"] = WiFi.status() == WL_CONNECTED;
    doc["ip"] = WiFi.status() == WL_CONNECTED ? WiFi.localIP().toString() : "";
    doc["rssi"] = WiFi.status() == WL_CONNECTED ? WiFi.RSSI() : 0;
    doc["taHost"] = tallyHost; doc["taPort"] = tallyPort; doc["taConnected"] = socketConnected;
    doc["setupAp"] = configApActive;
    doc["atemState"] = stateShort(atemState); doc["vmixState"] = stateShort(vmixState);
    doc["wifiReconnects"] = wifiReconnectCount; doc["taReconnects"] = taReconnectCount; doc["socketDisconnects"] = socketDisconnectCount;
    doc["lastDisconnect"] = lastDisconnectReason;
    String out; serializeJson(doc, out); configServer.send(200, "application/json", out);
  });
  configServer.on("/reboot", HTTP_POST, [](){ configServer.send(200, "text/html", "<p>CameraCue startet neu...</p>"); scheduleRestart(); });
  configServer.onNotFound([](){ configServer.sendHeader("Location", "/"); configServer.send(302); });
  configServer.begin();
  configServerStarted = true;
  Serial.println("Settings web server started on port 80");
}

void startNonDestructiveSetupAp() {
  if (configApActive) return;
  WiFi.mode(WIFI_AP_STA);
  configureWifiForReliability();
  String apName = listenerName + "-Setup";
  if (WiFi.softAP(apName.c_str(), "cameracue")) {
    configApActive = true;
    startConfigWebServer();
    Serial.println("Config AP active: " + apName + " / http://" + WiFi.softAPIP().toString());
    StickCP2.Display.fillScreen(TFT_NAVY);
    StickCP2.Display.setTextColor(TFT_WHITE, TFT_NAVY);
    StickCP2.Display.setTextDatum(top_left);
    StickCP2.Display.drawString("SETUP-MODUS (ohne Reset)", 5, 10);
    StickCP2.Display.drawString("WLAN: " + apName, 5, 30);
    StickCP2.Display.drawString("Passwort: cameracue", 5, 48);
    StickCP2.Display.drawString("Browser: 192.168.4.1", 5, 66);
    StickCP2.Display.drawString("Bestehende Config bleibt erhalten", 5, 88);
  }
}

bool connectWifiAndConfigure() {
  WiFi.mode(WIFI_STA);
  configureWifiForReliability();
  WiFi.setAutoReconnect(true);

  if (applyPendingWifiIfNeeded()) return true;

  // Once CameraCue has been configured, do not force the user through the
  // WiFiManager captive portal just because the configured WLAN is currently
  // unavailable. Try the saved credentials briefly and then fall back to our
  // non-destructive Setup AP from setup().
  if (hasConfiguration) {
    String savedSsid = storedWifiSsid();
    if (!savedSsid.isEmpty()) {
      Serial.println("Connecting to saved WiFi: " + savedSsid);
      WiFi.begin();
      if (waitForWifi(15000)) {
        configureWifiForReliability();
        return true;
      }
      Serial.println("Saved WiFi unavailable; CameraCue Setup AP will be opened.");
    } else {
      Serial.println("No saved WiFi credentials; CameraCue Setup AP will be opened.");
    }
    return false;
  }

  char hostBuf[64]; char portBuf[8]; char atemBuf[40]; char vmixBuf[40];
  tallyHost.toCharArray(hostBuf, sizeof(hostBuf));
  snprintf(portBuf, sizeof(portBuf), "%u", tallyPort);
  atemSourceHint.toCharArray(atemBuf, sizeof(atemBuf));
  vmixSourceHint.toCharArray(vmixBuf, sizeof(vmixBuf));

  paramTaHost = new WiFiManagerParameter("taHost", "Tally Arbiter IP/Host", hostBuf, 63);
  paramTaPort = new WiFiManagerParameter("taPort", "Tally Arbiter Port", portBuf, 7);
  paramAtemHint = new WiFiManagerParameter("atemHint", "ATEM Source Name (optional)", atemBuf, 39);
  paramVmixHint = new WiFiManagerParameter("vmixHint", "vMix Source Name (optional)", vmixBuf, 39);
  wifiManager.addParameter(paramTaHost); wifiManager.addParameter(paramTaPort);
  wifiManager.addParameter(paramAtemHint); wifiManager.addParameter(paramVmixHint);
  wifiManager.setSaveParamsCallback(savePortalParams);
  wifiManager.setHostname(listenerName.c_str());
  wifiManager.setConfigPortalTimeout(180);
  wifiManager.setClass("invert");

  StickCP2.Display.fillScreen(TFT_BLACK);
  StickCP2.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  StickCP2.Display.setTextDatum(top_left);
  StickCP2.Display.setTextSize(1);
  StickCP2.Display.drawString("CameraCue first setup", 5, 8);
  StickCP2.Display.drawString("Connect to:", 5, 26);
  StickCP2.Display.drawString(listenerName, 5, 42);
  StickCP2.Display.drawString("AP password: cameracue", 5, 58);

  bool ok = wifiManager.startConfigPortal(listenerName.c_str(), "cameracue");
  if (ok) savePortalParams();
  configureWifiForReliability();
  return ok;
}

void setupOta() {
  ArduinoOTA.setHostname(listenerName.c_str());
  ArduinoOTA.setPassword("cameracue");
  ArduinoOTA.onStart([]() {
    StickCP2.Display.fillScreen(TFT_NAVY);
    StickCP2.Display.setTextColor(TFT_WHITE, TFT_NAVY);
    StickCP2.Display.setTextDatum(middle_center);
    StickCP2.Display.drawString("OTA update...", 120, 67);
  });
  ArduinoOTA.onEnd([]() {
    StickCP2.Display.fillScreen(TFT_GREEN);
  });
  ArduinoOTA.begin();
}

void connectTallyArbiter() {
  if (WiFi.status() != WL_CONNECTED) return;
  socket.onEvent(onSocketEvent);
  socket.setReconnectInterval(2000);
  lastTaReconnectAttempt = millis();
  Serial.println("Connecting Socket.IO to " + tallyHost + ":" + String(tallyPort));
  // Tally Arbiter 3.3.0 explicitly keeps Engine.IO v3 compatibility enabled.
  // Pinning the path here avoids an accidental library-default change later.
  socket.begin(tallyHost.c_str(), tallyPort, "/socket.io/?EIO=3");
}

void restartTallyConnection(const String& reason = "TA reconnect") {
  if (WiFi.status() != WL_CONNECTED) return;
  socketConnected = false;
  atemState = TallyState::Unknown;
  vmixState = TallyState::Unknown;
  lastDisconnectReason = reason;
  ++taReconnectCount;
  socket.disconnect();
  delay(20);
  connectTallyArbiter();
  render();
}

void printSerialStatus() {
  Serial.println("--- CameraCue status ---");
  Serial.println("Version: " + String(CAMERA_CUE_VERSION));
  Serial.println("Device: " + deviceName + " / " + deviceId);
  Serial.println("WiFi: " + String(WiFi.status() == WL_CONNECTED ? "OK " : "OFF ") + (WiFi.status() == WL_CONNECTED ? WiFi.localIP().toString() : ""));
  if (WiFi.status() == WL_CONNECTED) Serial.println("SSID: " + WiFi.SSID() + " RSSI: " + String(WiFi.RSSI()) + " dBm");
  Serial.println("Tally Arbiter: " + tallyHost + ":" + String(tallyPort) + (socketConnected ? " OK" : " OFF"));
  Serial.println("ATEM: " + (atemDeviceSourceId.isEmpty() ? String("NO MAP") : atemSourceName + " @" + atemAddress + " " + stateShort(atemState)));
  Serial.println("vMix: " + (vmixDeviceSourceId.isEmpty() ? String("NO MAP") : vmixSourceName + " @" + vmixAddress + " " + stateShort(vmixState)));
  Serial.println("Reconnects WiFi=" + String(wifiReconnectCount) + " TA=" + String(taReconnectCount) + " socketDisconnects=" + String(socketDisconnectCount));
  Serial.println("Last disconnect: " + lastDisconnectReason);
  if (WiFi.status() == WL_CONNECTED) Serial.println("Settings: http://" + WiFi.localIP().toString());
}

void processSerialCommand(String cmd) {
  cmd.trim();
  if (cmd.isEmpty()) return;
  if (cmd == "help" || cmd == "?") {
    Serial.println("Commands: status | config | setup | reboot | set ta <host> <port> | set wifi <ssid> <password>");
    return;
  }
  if (cmd == "status" || cmd == "config" || cmd == "show") {
    printSerialStatus();
    return;
  }
  if (cmd == "setup") {
    startNonDestructiveSetupAp();
    Serial.println("Setup AP opened. Password: cameracue, URL: http://192.168.4.1");
    return;
  }
  if (cmd == "reboot") {
    Serial.println("Rebooting...");
    scheduleRestart(300);
    return;
  }
  if (cmd.startsWith("set ta ")) {
    String rest = cmd.substring(7); rest.trim();
    int split = rest.lastIndexOf(' ');
    if (split <= 0) { Serial.println("Usage: set ta <host> <port>"); return; }
    String host = rest.substring(0, split); host.trim();
    int port = rest.substring(split + 1).toInt();
    if (host.isEmpty() || port <= 0 || port > 65535) { Serial.println("Invalid host/port"); return; }
    tallyHost = host; tallyPort = static_cast<uint16_t>(port);
    prefs.begin("cameracue", false); prefs.putString("taHost", tallyHost); prefs.putUShort("taPort", tallyPort); prefs.putBool("configured", true); prefs.end();
    Serial.println("Tally Arbiter updated. Rebooting...");
    scheduleRestart(500);
    return;
  }
  if (cmd.startsWith("set wifi ")) {
    String rest = cmd.substring(9); rest.trim();
    int split = rest.indexOf(' ');
    if (split <= 0) { Serial.println("Usage: set wifi <ssid> <password> (SSID must not contain spaces in CLI; use web UI otherwise)"); return; }
    String ssid = rest.substring(0, split); ssid.trim();
    String pass = rest.substring(split + 1); pass.trim();
    if (ssid.isEmpty()) { Serial.println("SSID missing"); return; }
    prefs.begin("cameracue", false);
    prefs.putBool("wifiPending", true);
    prefs.putString("newSsid", ssid);
    prefs.putString("newPass", pass);
    prefs.putString("oldSsid", WiFi.status() == WL_CONNECTED ? WiFi.SSID() : storedWifiSsid());
    prefs.putString("oldPass", storedWifiPassword());
    prefs.end();
    Serial.println("New WiFi staged. CameraCue will test it and roll back automatically if it fails.");
    scheduleRestart(500);
    return;
  }
  Serial.println("Unknown command. Type 'help'.");
}

void handleSerialCli() {
  static String line;
  while (Serial.available()) {
    char c = static_cast<char>(Serial.read());
    if (c == '\r') continue;
    if (c == '\n') {
      processSerialCommand(line);
      line = "";
    } else if (line.length() < 180) {
      line += c;
    }
  }
}

void maintainWifi() {
  const bool connected = WiFi.status() == WL_CONNECTED;
  const unsigned long now = millis();

  if (connected && !wifiWasConnected) {
    wifiWasConnected = true;
    configureWifiForReliability();
    WiFi.setAutoReconnect(true);
    Serial.println("WiFi restored: " + WiFi.localIP().toString());
    restartTallyConnection("WiFi restored / TA resync");
  } else if (!connected && wifiWasConnected) {
    wifiWasConnected = false;
    socketConnected = false;
    atemState = TallyState::Unknown;
    vmixState = TallyState::Unknown;
    lastDisconnectReason = "WiFi lost";
    ++wifiReconnectCount;
    Serial.println("WiFi lost; Socket.IO paused and WiFi reconnect started");
    socket.disconnect();
    render();
  }

  if (!connected && now - lastWifiReconnectAttempt >= kWifiReconnectMs) {
    lastWifiReconnectAttempt = now;
    WiFi.reconnect();
  }

  // The library already has its own short reconnect interval. This watchdog is
  // deliberately slower and performs a clean reconnect if that mechanism gets
  // stuck while WiFi itself is healthy.
  if (connected && !socketConnected && now - lastTaReconnectAttempt >= kTaReconnectMs) {
    Serial.println("TA reconnect watchdog: rebuilding Socket.IO connection");
    restartTallyConnection("TA reconnect watchdog");
  }
}

} // namespace

void setup() {
  Serial.begin(115200);
  delay(50);

  auto cfg = M5.config();
  StickCP2.begin(cfg);
  StickCP2.Display.setRotation(kDisplayRotation);
  StickCP2.Display.setBrightness(brightnessLevels[brightnessIndex]);
  // Use M5Unified's native button objects directly. CameraCue does not modify
  // Button_Class hold thresholds: the 0.1.1 boot crash happened in that path,
  // and our combo timing is measured explicitly with millis() below.

  // Reliability first: 160 MHz leaves comfortable headroom for WiFi, Socket.IO,
  // the web settings server and display rendering without forcing max 240 MHz.
  setCpuFrequencyMhz(160);
  btStop();

  loadPreferences();
  StickCP2.Display.setBrightness(brightnessLevels[brightnessIndex]);

  uint64_t mac = ESP.getEfuseMac();
  char suffix[7];
  snprintf(suffix, sizeof(suffix), "%06llX", static_cast<unsigned long long>(mac & 0xFFFFFF));
  listenerName = String("CameraCue-") + suffix;

  Serial.printf("CameraCue %s / %s\n", CAMERA_CUE_VERSION, listenerName.c_str());

  if (!connectWifiAndConfigure()) {
    lastDisconnectReason = "WiFi unavailable - Setup AP";
    Serial.println("WiFi unavailable; opening non-destructive CameraCue Setup AP");
    startNonDestructiveSetupAp();
  }

  wifiWasConnected = WiFi.status() == WL_CONNECTED;
  setupOta();
  startConfigWebServer();
  if (wifiWasConnected) connectTallyArbiter();
  render();
}

void loop() {
  M5.update();
  handleSerialCli();
  ArduinoOTA.handle();
  configServer.handleClient();
  maintainWifi();
  if (WiFi.status() == WL_CONNECTED) socket.loop();

  if (scheduledRestartAt != 0 && static_cast<int32_t>(millis() - scheduledRestartAt) >= 0) {
    delay(50);
    ESP.restart();
  }

  const bool bothPressed = M5.BtnA.isPressed() && M5.BtnB.isPressed();
  if (bothPressed) {
    if (bothButtonsSince == 0) bothButtonsSince = millis();
    const uint32_t held = millis() - bothButtonsSince;
    if (held >= kSetupHoldMs && !comboConsumed) {
      comboConsumed = true;
      startNonDestructiveSetupAp();
    }
    if (held >= kFactoryResetHoldMs && !factoryResetPrompt) {
      factoryResetPrompt = true;
      StickCP2.Display.fillScreen(TFT_RED);
      StickCP2.Display.setTextColor(TFT_WHITE, TFT_RED);
      StickCP2.Display.setTextDatum(middle_center);
      StickCP2.Display.drawString("FACTORY RESET?", 120, 48);
      StickCP2.Display.drawString("Tasten loslassen", 120, 66);
      StickCP2.Display.drawString("danach A=JA  B=NEIN", 120, 84);
    }
  } else {
    bothButtonsSince = 0;
  }

  if (factoryResetPrompt && !bothPressed) {
    if (M5.BtnA.wasClicked()) {
      factoryResetConfiguration();
      return;
    }
    if (M5.BtnB.wasClicked()) {
      factoryResetPrompt = false;
      comboConsumed = false;
      render();
      return;
    }
  } else if (!bothPressed && !comboConsumed) {
    if (M5.BtnA.wasClicked()) {
      showDiagnostics = !showDiagnostics;
      render();
    }
    if (M5.BtnB.wasClicked()) cycleBrightness();
  }

  // Suppress the A/B click events generated when a combo is released.
  if (!bothPressed && comboConsumed && !factoryResetPrompt && !M5.BtnA.wasClicked() && !M5.BtnB.wasClicked()) {
    comboConsumed = false;
  }

  unsigned long now = millis();
  if (socketConnected && now - lastMetadataPoll >= kMetadataPollMs) {
    lastMetadataPoll = now;
    requestMetadata();
  }
  if (now - lastRender >= kRenderHeartbeatMs) render();
  delay(5);
}
