#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace radio_frame {
constexpr size_t packetLimit = 900;
constexpr size_t fragmentSize = 200;
constexpr size_t headerSize = 8;
constexpr uint32_t magic = 0x31525054;

inline size_t encode(const uint8_t* payload, size_t length, uint16_t id, uint8_t part, uint8_t* output) {
  const size_t offset = part * fragmentSize;
  const size_t bytes = length - offset < fragmentSize ? length - offset : fragmentSize;
  memcpy(output, &magic, 4);
  memcpy(output + 4, &id, 2);
  output[6] = part;
  output[7] = (length + fragmentSize - 1) / fragmentSize;
  memcpy(output + headerSize, payload + offset, bytes);
  return headerSize + bytes;
}

struct Assembly {
  uint8_t address[6]{};
  char payload[packetLimit + 1]{};
  uint16_t id = 0;
  uint8_t received = 0, count = 0;
  uint32_t updated = 0;
};

template <size_t N>
const char* receive(Assembly (&entries)[N], const uint8_t* address, const uint8_t* frame, size_t length, uint32_t now) {
  if (length <= headerSize || length > headerSize + fragmentSize) return nullptr;
  uint32_t marker;
  uint16_t id;
  memcpy(&marker, frame, 4);
  memcpy(&id, frame + 4, 2);
  const uint8_t part = frame[6], count = frame[7];
  const size_t bytes = length - headerSize, offset = part * fragmentSize;
  if (marker != magic || count < 1 || count > (packetLimit + fragmentSize - 1) / fragmentSize ||
      part >= count || offset + bytes > packetLimit || (part + 1 < count && bytes != fragmentSize)) return nullptr;
  Assembly* active = nullptr;
  for (auto& entry : entries) {
    if (entry.received && now - entry.updated > 350) entry.received = 0;
    if (entry.received && memcmp(entry.address, address, 6) == 0) { active = &entry; break; }
    if (!entry.received && !active) active = &entry;
  }
  if (!active || (part != 0 && (!active->received || id != active->id || count != active->count ||
      active->received != part || memcmp(active->address, address, 6) != 0))) return nullptr;
  if (part == 0) {
    memcpy(active->address, address, 6);
    active->id = id;
    active->count = count;
  }
  active->updated = now;
  memcpy(active->payload + offset, frame + headerSize, bytes);
  active->received = part + 1;
  if (part + 1 != count) return nullptr;
  active->received = 0;
  active->payload[offset + bytes] = 0;
  return active->payload;
}
}
