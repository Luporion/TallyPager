#pragma once
#include <Arduino.h>

namespace radio {
constexpr size_t packetLimit = 900;
constexpr uint8_t zeroAddress[6]{};
bool parseAddress(const String& text, uint8_t (&address)[6]);
String formatAddress(const uint8_t* address);
bool start(const String& mode, int channel, bool gateway, const uint8_t* peer);
bool ready();
bool send(const uint8_t* address, const String& payload);
bool read(uint8_t* address, char (&payload)[packetLimit + 1]);
void poll();
}
