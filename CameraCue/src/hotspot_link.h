#pragma once

#include <Arduino.h>
#include <WiFiUdp.h>

namespace hotspot {
constexpr uint16_t tallyPort = 46224;
constexpr size_t packetLimit = 900;
constexpr size_t envelopeSize = 21;
constexpr int maxClients = 4;
constexpr char linkPrefix[] = "TP LINK ";
constexpr char wifiPrefix[] = "TP WIFI ";
const IPAddress gateway(192, 168, 4, 1), mask(255, 255, 255, 0);

inline bool validChannel(int channel) { return channel == 1 || channel == 6 || channel == 11; }

inline bool decodeAddress(const char* text, uint8_t* bytes) {
  for (size_t i = 0; i < 12; ++i) {
    char ch = text[i];
    int digit = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : -1;
    if (digit < 0) return false;
    bytes[i / 2] = (i % 2 ? bytes[i / 2] << 4 : 0) | digit;
  }
  return true;
}

inline bool tallyPayload(const char* bytes, size_t length) {
  if (length < 4 || length > packetLimit || memcmp(bytes, "TP1|", 4) != 0) return false;
  for (size_t i = 4; i < length; ++i)
    if (bytes[i] < ' ' || bytes[i] > '~') return false;
  return true;
}

class Link {
  WiFiUDP traffic;

  static bool clientAddress(const IPAddress& ip) {
    return ip[0] == gateway[0] && ip[1] == gateway[1] && ip[2] == gateway[2] && ip[3] >= 2 && ip[3] <= 254;
  }

public:
  bool begin() {
#ifdef ESP8266
    // This firmware enables only the AP interface when offering UDP.
    return traffic.begin(tallyPort);
#else
    return traffic.begin(gateway, tallyPort);
#endif
  }

  void fromSerial(const char* line, size_t length, const char* prefix) {
    if (length <= envelopeSize || strncmp(line, prefix, 8) != 0 || line[20] != ' ' ||
        !tallyPayload(line + envelopeSize, length - envelopeSize)) return;
    uint8_t bytes[6];
    if (!decodeAddress(line + 8, bytes)) return;
    const IPAddress ip(bytes[0], bytes[1], bytes[2], bytes[3]);
    const uint16_t port = (bytes[4] << 8) | bytes[5];
    if (clientAddress(ip) && port && traffic.beginPacket(ip, port)) {
      traffic.write(reinterpret_cast<const uint8_t*>(line + envelopeSize), length - envelopeSize);
      traffic.endPacket();
    }
  }

  void toSerial(const char* prefix) {
    char packet[packetLimit];
    for (int i = 0; i < 4; ++i) {
      int size = traffic.parsePacket();
      if (size <= 0) return;
      IPAddress ip = traffic.remoteIP();
      uint16_t port = traffic.remotePort();
      if (size > static_cast<int>(sizeof(packet)) || !clientAddress(ip) || !port) {
#ifndef ESP8266
        // ESP8266 advances/discards the previous datagram in parsePacket().
        traffic.flush();
#endif
        continue;
      }
      if (traffic.read(reinterpret_cast<uint8_t*>(packet), size) != size || !tallyPayload(packet, size)) continue;
      char header[envelopeSize + 1];
      snprintf(header, sizeof(header), "%s%02x%02x%02x%02x%04x ", prefix, ip[0], ip[1], ip[2], ip[3], port);
      Serial.write(reinterpret_cast<const uint8_t*>(header), envelopeSize);
      Serial.write(reinterpret_cast<const uint8_t*>(packet), size);
      Serial.write('\n');
    }
  }
};
}
