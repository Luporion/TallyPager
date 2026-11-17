#include "radio.h"
#include <BLE2902.h>
#include <BLEDevice.h>
#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <algorithm>
#include <atomic>

namespace radio {
namespace {
constexpr size_t fragmentSize = 200;
constexpr size_t headerSize = 8;
constexpr size_t maxPeers = 6;
constexpr uint32_t magic = 0x31525054;
const char* serviceUuid = "d28b0400-d09a-4f12-bcdf-17a46b560e00";
const char* downUuid = "d28b0401-d09a-4f12-bcdf-17a46b560e00";
const char* upUuid = "d28b0402-d09a-4f12-bcdf-17a46b560e00";

struct Frame {
  uint8_t address[6];
  uint16_t length;
  uint8_t bytes[headerSize + fragmentSize];
};
struct Packet {
  uint8_t address[6];
  uint16_t length;
  uint8_t bytes[packetLimit];
};
struct Assembly {
  Packet packet;
  uint16_t id = 0;
  uint32_t updated = 0;
  uint8_t received = 0;
};
QueueHandle_t incoming, outgoing, discoveries;
Assembly assemblies[maxPeers];
uint8_t target[6]{};
std::atomic<bool> started{false}, scanEnded{true};
bool gateway, ble;
uint16_t packetId;
Packet sending;
uint8_t segment = 0;
uint32_t waitingAt = 0;
std::atomic<int> sendResult{0};
BLECharacteristic* up;
BLEServer* server;

void enqueue(const uint8_t* address, const uint8_t* data, size_t length) {
  if (length <= headerSize || length > sizeof(Frame::bytes)) return;
  Frame frame{};
  memcpy(frame.address, address, 6);
  memcpy(frame.bytes, data, length);
  frame.length = length;
  xQueueSend(incoming, &frame, 0);
}

void receiveNow(const uint8_t* address, const uint8_t* data, int length) {
  if (gateway || memcmp(target, address, 6) == 0) enqueue(address, data, length);
}

void notify(BLERemoteCharacteristic* characteristic, uint8_t* data, size_t length, bool) {
  auto address = characteristic->getRemoteService()->getClient()->getPeerAddress();
  enqueue(*address.getNative(), data, length);
}

class PagerWrite : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic* characteristic) override {
    const auto& value = characteristic->getValue();
    enqueue(zeroAddress, reinterpret_cast<const uint8_t*>(value.data()), value.size());
  }
};

class PagerConnection : public BLEServerCallbacks {
  void onDisconnect(BLEServer*) override { BLEDevice::startAdvertising(); }
};

struct Candidate { uint8_t address[6]; esp_ble_addr_type_t type; };
class ScanDevices : public BLEAdvertisedDeviceCallbacks {
  void onResult(BLEAdvertisedDevice device) override {
    if (!device.haveServiceUUID() || !device.isAdvertisingService(BLEUUID(serviceUuid))) return;
    Candidate entry{};
    auto address = device.getAddress();
    memcpy(entry.address, *address.getNative(), 6);
    entry.type = device.getAddressType();
    xQueueSend(discoveries, &entry, 0);
  }
};

struct BlePeer { BLEClient* client = nullptr; BLERemoteCharacteristic* down = nullptr; };
BlePeer blePeers[3];

BlePeer* peerFor(const uint8_t* address) {
  for (auto& peer : blePeers) {
    if (!peer.client || !peer.client->isConnected()) continue;
    auto remote = peer.client->getPeerAddress();
    if (memcmp(*remote.getNative(), address, 6) == 0) return &peer;
  }
  return nullptr;
}

void connectCandidate(const Candidate& entry) {
  if (peerFor(entry.address)) return;
  for (auto& peer : blePeers) {
    if (peer.client && peer.client->isConnected()) continue;
    if (!peer.client) peer.client = BLEDevice::createClient();
    uint8_t address[6];
    memcpy(address, entry.address, 6);
    if (!peer.client->connect(BLEAddress(address), entry.type)) return;
    peer.client->setMTU(247);
    auto* service = peer.client->getService(BLEUUID(serviceUuid));
    auto* down = service ? service->getCharacteristic(BLEUUID(downUuid)) : nullptr;
    auto* remoteUp = service ? service->getCharacteristic(BLEUUID(upUuid)) : nullptr;
    if (peer.client->getMTU() < headerSize + fragmentSize + 3 || !down || !down->canWrite() ||
        !remoteUp || !remoteUp->canNotify()) {
      peer.client->disconnect();
      return;
    }
    peer.down = down;
    remoteUp->registerForNotify(notify);
    return;
  }
}

size_t build(const Packet& packet, int part, uint8_t* output) {
  size_t offset = part * fragmentSize;
  memcpy(output, &magic, 4);
  memcpy(output + 4, &packetId, 2);
  output[6] = part;
  output[7] = (packet.length + fragmentSize - 1) / fragmentSize;
  size_t bytes = min(fragmentSize, packet.length - offset);
  memcpy(output + headerSize, packet.bytes + offset, bytes);
  return headerSize + bytes;
}

void bleTask(void*) {
  BLEScan* scanner = BLEDevice::getScan();
  if (gateway) scanner->setAdvertisedDeviceCallbacks(new ScanDevices());
  uint32_t lastScan = 0;
  while (true) {
    Packet packet{};
    if (xQueueReceive(outgoing, &packet, pdMS_TO_TICKS(10))) {
      BlePeer* peer = gateway ? peerFor(packet.address) : nullptr;
      if ((gateway && peer && peer->down) || (!gateway && server->getConnectedCount())) {
        ++packetId;
        for (int part = 0; static_cast<size_t>(part) * fragmentSize < packet.length; ++part) {
          uint8_t frame[headerSize + fragmentSize];
          size_t bytes = build(packet, part, frame);
          if (gateway) {
            if (!peer->client->isConnected()) break;
            peer->down->writeValue(frame, bytes, true);
          } else {
            if (!server->getConnectedCount()) break;
            up->setValue(frame, bytes);
            up->notify();
            vTaskDelay(pdMS_TO_TICKS(3));
          }
        }
      }
    }
    if (!gateway) continue;
    if (scanEnded.load()) {
      Candidate entry{};
      if (xQueueReceive(discoveries, &entry, 0)) connectCandidate(entry);
      else if ((!lastScan || millis() - lastScan >= 5000) &&
          std::any_of(std::begin(blePeers), std::end(blePeers), [](const BlePeer& p) {
            return !p.client || !p.client->isConnected();
          })) {
        lastScan = millis();
        scanner->clearResults();
        scanEnded = false;
        if (!scanner->start(1, [](BLEScanResults) { scanEnded = true; }, false)) scanEnded = true;
      }
    }
  }
}

bool addPeer(const uint8_t* address) {
  if (esp_now_is_peer_exist(address)) return true;
  esp_now_peer_info_t peer{};
  memcpy(peer.peer_addr, address, 6);
  peer.ifidx = WIFI_IF_STA;
  return esp_now_add_peer(&peer) == ESP_OK;
}
}

bool parseAddress(const String& text, uint8_t (&address)[6]) {
  if (text.length() != 12) return false;
  for (int i = 0; i < 6; ++i) {
    auto digit = [](char c) { return c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1; };
    int high = digit(text[i * 2]), low = digit(text[i * 2 + 1]);
    if (high < 0 || low < 0) return false;
    address[i] = high * 16 + low;
  }
  return !(address[0] & 1) && memcmp(address, zeroAddress, 6) != 0;
}

String formatAddress(const uint8_t* address) {
  char hex[13];
  snprintf(hex, sizeof(hex), "%02x%02x%02x%02x%02x%02x",
      address[0], address[1], address[2], address[3], address[4], address[5]);
  return hex;
}

bool start(const String& mode, int channel, bool base, const uint8_t* peer) {
  gateway = base;
  ble = mode == "ble";
  memcpy(target, peer, 6);
  incoming = xQueueCreate(32, sizeof(Frame));
  outgoing = xQueueCreate(10, sizeof(Packet));
  if (!incoming || !outgoing) return false;
  packetId = esp_random();
  if (ble) {
    discoveries = xQueueCreate(8, sizeof(Candidate));
    if (!discoveries) return false;
    BLEDevice::init(base ? "TallyPager Basis" : "TallyPager");
    BLEDevice::setMTU(247);
    if (!base) {
      server = BLEDevice::createServer();
      server->setCallbacks(new PagerConnection());
      auto* service = server->createService(serviceUuid);
      auto* down = service->createCharacteristic(downUuid, BLECharacteristic::PROPERTY_WRITE);
      down->setCallbacks(new PagerWrite());
      up = service->createCharacteristic(upUuid, BLECharacteristic::PROPERTY_NOTIFY);
      up->addDescriptor(new BLE2902());
      service->start();
      BLEAdvertisementData advertisement;
      advertisement.setFlags(0x06);
      advertisement.setCompleteServices(BLEUUID(serviceUuid));
      BLEDevice::getAdvertising()->setAdvertisementData(advertisement);
      BLEDevice::startAdvertising();
    }
    started = xTaskCreate(bleTask, "tallyBLE", 6144, nullptr, 2, nullptr) == pdPASS;
    return started;
  }
  WiFi.persistent(false);
  if (!WiFi.mode(WIFI_STA)) return false;
  WiFi.setAutoReconnect(false);
  WiFi.setSleep(false);
  esp_wifi_disconnect();
  const uint8_t protocols = mode == "espnow-lr" ? WIFI_PROTOCOL_LR :
      WIFI_PROTOCOL_11B | WIFI_PROTOCOL_11G | WIFI_PROTOCOL_11N;
  if (esp_wifi_set_protocol(WIFI_IF_STA, protocols) != ESP_OK ||
      esp_wifi_set_channel(channel, WIFI_SECOND_CHAN_NONE) != ESP_OK || esp_now_init() != ESP_OK) return false;
  if (mode == "espnow-lr" && esp_wifi_config_espnow_rate(WIFI_IF_STA, WIFI_PHY_RATE_LORA_250K) != ESP_OK)
    return false;
  started = esp_now_register_recv_cb(receiveNow) == ESP_OK &&
      esp_now_register_send_cb([](const uint8_t*, esp_now_send_status_t result) {
        sendResult = result == ESP_NOW_SEND_SUCCESS ? 1 : -1;
      }) == ESP_OK;
  return started;
}

bool ready() { return started; }

bool send(const uint8_t* address, const String& payload) {
  if (!started || payload.isEmpty() || payload.length() > packetLimit) return false;
  Packet packet{};
  memcpy(packet.address, address, 6);
  packet.length = payload.length();
  memcpy(packet.bytes, payload.c_str(), packet.length);
  return xQueueSend(outgoing, &packet, 0) == pdPASS;
}

bool read(uint8_t* address, char (&payload)[packetLimit + 1]) {
  Frame frame{};
  for (int i = 0; i < 32 && incoming && xQueueReceive(incoming, &frame, 0); ++i) {
    uint32_t marker;
    uint16_t id;
    memcpy(&marker, frame.bytes, 4);
    memcpy(&id, frame.bytes + 4, 2);
    uint8_t part = frame.bytes[6], count = frame.bytes[7];
    size_t bytes = frame.length - headerSize, offset = part * fragmentSize;
    if (marker != magic || count < 1 || count > (packetLimit + fragmentSize - 1) / fragmentSize ||
        part >= count || offset + bytes > packetLimit || (part + 1 < count && bytes != fragmentSize)) continue;
    uint32_t now = millis();
    Assembly* active = nullptr;
    for (auto& entry : assemblies) {
      if (entry.received && now - entry.updated > 350) entry.received = 0;
      if (memcmp(entry.packet.address, frame.address, 6) == 0 && entry.received) { active = &entry; break; }
      if (!entry.received && !active) active = &entry;
    }
    if (!active || (part != 0 && (!active->received || id != active->id ||
        memcmp(active->packet.address, frame.address, 6) != 0 || active->received != part))) continue;
    if (part == 0) {
      memcpy(active->packet.address, frame.address, 6);
      active->id = id;
    }
    active->updated = now;
    memcpy(active->packet.bytes + offset, frame.bytes + headerSize, bytes);
    active->received = part + 1;
    if (part + 1 != count) continue;
    active->received = 0;
    memcpy(address, frame.address, 6);
    memcpy(payload, active->packet.bytes, offset + bytes);
    payload[offset + bytes] = 0;
    return true;
  }
  return false;
}

void poll() {
  if (!started || ble) return;
  if (waitingAt) {
    int result = sendResult.exchange(0);
    if (!result) {
      // Avoid starting a new frame with an unresolved callback.
      if (millis() - waitingAt < 300) return;
      sending.length = 0;
    } else if (result < 0 || static_cast<size_t>(++segment) * fragmentSize >= sending.length) {
      sending.length = 0;
    }
    waitingAt = 0;
  }
  if (!sending.length) {
    if (!xQueueReceive(outgoing, &sending, 0)) return;
    segment = 0;
    ++packetId;
  }
  uint8_t frame[headerSize + fragmentSize];
  size_t bytes = build(sending, segment, frame);
  if (addPeer(sending.address) && esp_now_send(sending.address, frame, bytes) == ESP_OK)
    waitingAt = max(1UL, millis());
  else sending.length = 0;
}
}
