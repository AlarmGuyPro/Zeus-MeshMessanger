# Getting started

## 1. Give Zeus its own node

Use a LoRa board (a Heltec V4 works well) that is only for Zeus: on your Wi-Fi,
powered from the shack, with nothing else connected to it. It becomes its own
station on your mesh, with its own name and its own messages, and reaches the
rest of the mesh through your repeaters like any other device.

- Name it clearly, e.g. `N0CALL Shack`. People message the station by this
  name. On MeshCore, a shorter name leaves more room in channel messages.
- Mount it, or at least its antenna, away from your HF antenna and feedline.
  Strong HF transmit signals can overload or damage it, and its Wi-Fi and
  power-supply noise can show up in your SDR.
- Give it a fixed address on your network (a DHCP reservation in your router).

## 2. Firmware

- **Meshtastic:** any recent firmware. Turn on Wi-Fi in the node's network
  settings with the official app. (On ESP32 boards, turning on Wi-Fi turns off
  Bluetooth, which is fine for a dedicated node.)
- **MeshCore:** the *companion radio Wi-Fi* build for your board
  (`heltec_v4_companion_radio_wifi` for a Heltec V4). MeshCore's Wi-Fi network
  name and password are built into this firmware, so it has to be built with
  your network's details. A MeshCore node talks to one app at a time; if you
  connect your phone or meshcore-cli to it, Zeus is disconnected until they
  let go.

## 3. Add the node in Zeus

In the Mesh Messenger panel: **Add node** → choose the network, enter its
address, give it a name. The node bar shows its state (`connecting`,
`connected`, `disconnected`, `error`) with the reason.

## 4. Join your channels

Join the same channels your other devices use (see [Channels and
rooms](channels-and-rooms.md)) so Zeus sees the same group traffic.

## 5. Send a message

Pick a conversation and type. The byte counter shows how much room is left;
the limit depends on the network (see [Messages](messages.md)).
