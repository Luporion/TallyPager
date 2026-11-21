#pragma once

#include <ESP8266WiFi.h>
#include <WiFiUdp.h>
#include <libb64/cdecode.h>
#include <libb64/cencode.h>
#include "hotspot_link.h"

namespace mumble {
constexpr uint16_t port = 64738;
constexpr size_t tcpChunk = 384, maxDatagram = 1024;
constexpr size_t headerSize = 16, maxLine = headerSize + ((maxDatagram + 2) / 3) * 4;
constexpr uint32_t timeout = 5000;
constexpr char prefix[] = "TP M ";

// Tally uses its existing serial frames. Mumble frames carry a connection ID
// and one control byte. TCP chunks require an ACK; UDP keeps datagram boundaries.
class Link {
  struct Peer {
    WiFiClient tcp;
    IPAddress address;
    uint32_t id = 0, sentAt = 0, pendingAt = 0;
    uint16_t udpPort = 0;
    size_t offset = 0, pending = 0;
    bool ready = false, awaitingAck = false;
    uint8_t incoming[tcpChunk];
  };
  WiFiServer server{hotspot::gateway, port};
  WiFiUDP udp;
  Peer peers[2];
  uint32_t nextId = 0, lastHost = 0;
  bool attached = false;
  unsigned nextPeer = 0;
  // Three decoded bytes per quartet, including padding space for a maximum-size packet.
  uint8_t packet[((maxDatagram + 2) / 3) * 3];
  char frame[maxLine + 1];

  void emit(uint32_t id, char op, const uint8_t* data = nullptr, size_t length = 0) {
    size_t count = snprintf(frame, sizeof(frame), "TP M %08lx %c", static_cast<unsigned long>(id), op);
    if (length) {
      frame[count++] = ' ';
      base64_encodestate state;
      base64_init_encodestate_nonewlines(&state);
      count += base64_encode_block(reinterpret_cast<const char*>(data), length, frame + count, &state);
      count += base64_encode_blockend(frame + count, &state);
    }
    frame[count++] = '\n';
    Serial.write(reinterpret_cast<const uint8_t*>(frame), count);
  }

  static bool addressAllowed(const IPAddress& address) {
    return address[0] == 192 && address[1] == 168 && address[2] == 4 && address[3] >= 2 && address[3] < 255;
  }

  void close(Peer& peer, bool notify) {
    if (!peer.id) return;
    if (notify) emit(peer.id, 'C');
    peer.tcp.stop(1);
    peer.tcp = WiFiClient();
    peer.id = 0;
  }

  bool accept(uint32_t now) {
    auto incoming = server.accept();
    if (!incoming) return false;
    auto address = incoming.remoteIP();
    Peer* available = nullptr;
    if (attached && addressAllowed(address)) {
      for (auto& peer : peers) {
        if (peer.id && peer.address == address) {
          close(peer, true);
          available = &peer;
          break;
        }
        if (!peer.id && !available) available = &peer;
      }
    }
    if (!available) {
      incoming.stop(1);
      return false;
    }
    auto& peer = *available;
    peer.tcp = incoming;
    peer.tcp.setTimeout(3);
    peer.address = address;
    if (++nextId == 0) ++nextId;
    peer.id = nextId;
    peer.sentAt = now;
    peer.udpPort = 0;
    peer.offset = peer.pending = 0;
    peer.ready = peer.awaitingAck = false;
    emit(peer.id, 'O');
    return true;
  }

  bool tcpTick(Peer& peer, uint32_t now) {
    if (!peer.id) return false;
    if (!peer.tcp.connected() ||
        ((!peer.ready || peer.awaitingAck) && now - peer.sentAt >= timeout) ||
        (peer.pending && now - peer.pendingAt >= timeout)) {
      close(peer, true);
      return true;
    }
    if (!peer.ready) return false;
    if (peer.pending) {
      int room = peer.tcp.availableForWrite();
      if (room > 0) {
        size_t count = peer.tcp.write(peer.incoming + peer.offset, std::min(peer.pending, static_cast<size_t>(room)));
        peer.pending -= count;
        peer.offset += count;
        if (!peer.pending) {
          emit(peer.id, 'A');
          return true;
        }
      }
    }
    if (peer.awaitingAck || peer.tcp.available() <= 0) return false;
    int count = peer.tcp.read(packet, tcpChunk);
    if (count <= 0) return false;
    emit(peer.id, 'D', packet, count);
    peer.awaitingAck = true;
    peer.sentAt = now;
    return true;
  }

  void udpTick() {
    int length = udp.parsePacket();
    if (length <= 0 || length > static_cast<int>(maxDatagram)) return;
    auto address = udp.remoteIP();
    uint16_t sourcePort = udp.remotePort();
    for (auto& peer : peers) {
      if (!peer.id || !peer.ready || peer.address != address) continue;
      if (udp.read(packet, length) != length) return;
      peer.udpPort = sourcePort;
      emit(peer.id, 'U', packet, length);
      return;
    }
  }

public:
  bool begin(uint32_t seed) {
    nextId = seed;
    server.begin(port, 2);
    server.setNoDelay(true);
    return server.status() && udp.begin(port);
  }

  void fromSerial(const char* line, size_t length) {
    if (length < headerSize - 1 || line[13] != ' ') return;
    uint32_t id = 0;
    for (size_t i = 5; i < 13; ++i) {
      char ch = line[i];
      if (ch >= '0' && ch <= '9') id = (id << 4) | (ch - '0');
      else if (ch >= 'a' && ch <= 'f') id = (id << 4) | (ch - 'a' + 10);
      else return;
    }
    char op = line[14];
    if (!id && length == headerSize - 1 && (op == 'R' || op == 'H')) {
      if (op == 'R') for (auto& peer : peers) close(peer, false);
      lastHost = millis();
      attached = true;
      return;
    }
    if (!attached || !id) return;
    for (auto& peer : peers) {
      if (peer.id != id) continue;
      if (length == headerSize - 1) {
        if (op == 'R') peer.ready = true;
        else if (op == 'A') peer.awaitingAck = false;
        else if (op == 'C') close(peer, false);
        return;
      }
      if (!peer.ready || (op != 'D' && op != 'U') || line[15] != ' ') return;
      size_t encoded = length - headerSize;
      size_t limit = op == 'D' ? tcpChunk : maxDatagram;
      if (!encoded || encoded % 4 || encoded > ((limit + 2) / 3) * 4) return;
      size_t symbols = encoded;
      while (encoded - symbols < 2 && line[headerSize + symbols - 1] == '=') --symbols;
      for (size_t i = 0; i < symbols; ++i)
        if (base64_decode_value(line[headerSize + i]) < 0) return;
      int count = base64_decode_chars(line + headerSize, encoded, reinterpret_cast<char*>(packet));
      if (count <= 0 || static_cast<size_t>(count) > limit) return;
      if (op == 'D') {
        if (peer.pending) { close(peer, true); return; }
        memcpy(peer.incoming, packet, count);
        peer.offset = 0;
        peer.pending = count;
        peer.pendingAt = millis();
      } else if (peer.udpPort && udp.beginPacket(peer.address, peer.udpPort)) {
        udp.write(packet, count);
        udp.endPacket();
      }
      return;
    }
  }

  void tick() {
    uint32_t now = millis();
    if (attached && now - lastHost >= timeout) {
      attached = false;
      for (auto& peer : peers) close(peer, true);
    }
    if (accept(now) || !attached) return;
    for (unsigned i = 0; i < 2; ++i) {
      auto& peer = peers[nextPeer];
      nextPeer = (nextPeer + 1) % 2;
      if (tcpTick(peer, now)) return;
    }
    udpTick();
  }
};
}
