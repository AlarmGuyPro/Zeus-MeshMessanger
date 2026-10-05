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
- A fixed address (DHCP reservation) is optional: Zeus finds the node again if its address changes.

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

In the Mesh Messenger panel: **Add node → Scan**. Zeus looks for Meshtastic
and MeshCore nodes on your network and lists them by name; pick yours and
press **Add**. You don't need to know its IP address or set a static address
in your router: Zeus remembers the node itself and finds it again if its
address changes.

**Nodes on another VLAN** (for example an IoT network): add that network's
range under **Settings → Nodes → Extra networks to scan**, e.g.
`192.168.30.0/24`. Your router must allow the Zeus PC to reach that network
on TCP ports 4403 (Meshtastic) and 5000 (MeshCore). If a range finds nothing
at all, that's usually the firewall.

You can still type an address or host name by hand under **Add manually**.

The node bar shows its state (`connecting`, `connected`, `searching`,
`disconnected`, `error`) with the reason.

## 4. Join your channels

Join the same channels your other devices use (see [Channels and
rooms](channels-and-rooms.md)) so Zeus sees the same group traffic.

## 5. Send a message

Pick a conversation and type. The byte counter shows how much room is left;
the limit depends on the network (see [Messages](messages.md)).
