# Meshtastic client API — what Mesh Messenger uses

Working notes for the Meshtastic connector, written from the upstream sources
below. Only the parts this feature uses are listed. Field numbers are what the
hand-written protobuf codec must match exactly.

Sources (read 2026-10-05):

- `meshtastic/protobufs` @ [`95c5f8c`](https://github.com/meshtastic/protobufs/tree/95c5f8c1c4223bb27faa81417b90560037f8ad31) — `meshtastic/mesh.proto`, `channel.proto`, `apponly.proto`, `admin.proto`, `telemetry.proto`, `portnums.proto`
- `meshtastic/python` @ [`0a18357`](https://github.com/meshtastic/python/tree/0a1835760c2437314a2d3544bbdd8b6fead559bc) — `stream_interface.py`, `tcp_interface.py`, `mesh_interface.py`, `node.py`, `util.py`

## Transport

- TCP to the node, default port **4403**.
- Each message in either direction is framed as:
  `0x94 0xC3 <len hi> <len lo> <protobuf bytes>` — length is big-endian and at
  most 512.
- Receiving: scan for `0x94`, then `0xC3`, read the 2-byte length, then the
  payload. Anything else is debug text from the device and is skipped; resync
  on the next `0x94 0xC3`.
- On connect the reference client first writes 32 bytes of `0xC3` to wake the
  device and reset its parser, waits ~100 ms, then starts the handshake.
- App → node messages are `ToRadio`; node → app are `FromRadio`.
- Keep-alive: send `ToRadio{ heartbeat {} }` periodically (reference client:
  every 300 s).

## Handshake

1. Send `ToRadio{ want_config_id = <random uint32> }`.
2. The node streams `FromRadio` messages: `my_info`, `metadata`, one
   `node_info` per known node, one `channel` per slot, `config` /
   `moduleConfig` sections, then `config_complete_id` equal to the id we sent.
3. After that, live traffic arrives as `FromRadio.packet` (and `queueStatus`,
   `clientNotification`, `log_record`, `rebooted`).

## Messages we encode/decode

`ToRadio` (oneof): `packet`=1 MeshPacket · `want_config_id`=3 uint32 ·
`disconnect`=4 bool · `heartbeat`=7 Heartbeat.

`FromRadio`: `id`=1 · oneof `packet`=2 · `my_info`=3 · `node_info`=4 ·
`config`=5 · `log_record`=6 · `config_complete_id`=7 uint32 · `rebooted`=8 ·
`moduleConfig`=9 · `channel`=10 · `queueStatus`=11 · `metadata`=13 ·
`clientNotification`=16. Unknown fields are skipped.

`MeshPacket`:

| Field | # | Type | Use |
|---|---|---|---|
| from | 1 | fixed32 | sender node number |
| to | 2 | fixed32 | `0xFFFFFFFF` = broadcast (channel message) |
| channel | 3 | uint32 | channel slot index |
| decoded | 4 | Data | payload (oneof with `encrypted`=5, which we ignore) |
| id | 6 | fixed32 | packet id; we choose it on send to match acks |
| rx_time | 7 | fixed32 | |
| rx_snr | 8 | float | SNR of the last hop, dB |
| hop_limit | 9 | uint32 | hops remaining |
| want_ack | 10 | bool | request delivery ack |
| rx_rssi | 12 | int32 | RSSI of the last hop, dBm |
| via_mqtt | 14 | bool | came through MQTT somewhere |
| hop_start | 15 | uint32 | starting hop limit |
| pki_encrypted | 17 | bool | DM was public-key encrypted |
| relay_node | 19 | uint32 | low byte of the last relayer |

**Hops travelled = `hop_start - hop_limit`.** Upstream warns that
`hop_start == 0` is only trustworthy on packets from firmware ≥ 2.5; show
"unknown" rather than "direct" when it is 0.

`Data`: `portnum`=1 · `payload`=2 bytes · `want_response`=3 · `dest`=4 ·
`source`=5 · `request_id`=6 fixed32 · `reply_id`=7 fixed32 · `emoji`=8.

Port numbers used: `TEXT_MESSAGE_APP`=1, `POSITION_APP`=3,
`NODEINFO_APP`=4, `ROUTING_APP`=5, `ADMIN_APP`=6, `TELEMETRY_APP`=67,
`TRACEROUTE_APP`=70, `NEIGHBORINFO_APP`=71.

`MyNodeInfo`: `my_node_num`=1. `DeviceMetadata`: `firmware_version`=1,
`hw_model`=9, `hasWifi`=4.

`NodeInfo`: `num`=1 · `user`=2 · `position`=3 · `snr`=4 float ·
`last_heard`=5 fixed32 · `device_metrics`=6 · `channel`=7 · `via_mqtt`=8 ·
`hops_away`=9 · `is_favorite`=10 · `is_ignored`=11.

`User`: `id`=1 (`!xxxxxxxx`) · `long_name`=2 (≤ 40 bytes) · `short_name`=3
(≤ 5 bytes) · `hw_model`=5 · `is_licensed`=6 · `role`=7 · `public_key`=8 ·
`is_unmessagable`=9.

`Position`: `latitude_i`=1 sfixed32 (×1e-7) · `longitude_i`=2 sfixed32 ·
`altitude`=3 · `time`=4.

`DeviceMetrics` (in `NodeInfo` and `Telemetry`): `battery_level`=1 ·
`voltage`=2 · `channel_utilization`=3 · `air_util_tx`=4 · `uptime_seconds`=5.

## Text messages

- Send: `MeshPacket{ to, channel, id, want_ack=true, decoded{ portnum=1,
  payload=<UTF-8> } }`. Broadcast to a channel uses `to = 0xFFFFFFFF` and the
  channel index; a DM uses the destination node number (firmware ≥ 2.5
  encrypts DMs with the recipient's public key when it knows it; the
  received packet's `pki_encrypted` shows this).
- Hard payload limit is `DATA_PAYLOAD_LEN` = **233 bytes**. The official apps
  cap text at 200 bytes; we use 200.
- Receive: `FromRadio.packet` with `decoded.portnum == 1`. Broadcast →
  channel conversation `(channel)`; addressed to us → DM conversation
  `(from)`. Capture `rx_snr`, `rx_rssi`, hops, `via_mqtt` for the message info
  line.

## Delivery status (acks)

With `want_ack`, the node later delivers a `ROUTING_APP` packet whose
`Data.request_id` equals our packet `id`. Decode its payload as `Routing`:
`error_reason`=3 — `NONE` (0) means delivered (for a DM) or relayed by at
least one node (for a broadcast). Other values to surface: `NO_ROUTE`=1,
`GOT_NAK`=2, `TIMEOUT`=3, `MAX_RETRANSMIT`=5, `NO_CHANNEL`=6, `TOO_LARGE`=7,
`DUTY_CYCLE_LIMIT`=9, `PKI_FAILED`=34, `PKI_UNKNOWN_PUBKEY`=35,
`RATE_LIMIT_EXCEEDED`=38.

`QueueStatus` (`res`=1, `free`=2, `maxlen`=3, `mesh_packet_id`=4) tells us the
node accepted a packet into its transmit queue.

## Traceroute

- Send `Data{ portnum=70, want_response=true, payload=<empty RouteDiscovery> }`
  to the target node with a hop limit (reference default 7 is the max).
- Reply is a `TRACEROUTE_APP` packet carrying `RouteDiscovery`:
  `route`=1 repeated fixed32 (node numbers towards the target),
  `snr_towards`=2 repeated int32, `route_back`=3, `snr_back`=4.
- SNR values are dB × 4; `-128` means unknown. Each SNR list has one more
  entry than its route (the final hop).
- If instead a `ROUTING_APP` packet with an error arrives, the trace failed
  (show the error).
- Rate-limit this in the UI: traceroutes are airtime-expensive and firmware
  throttles them.

## Channels (join / create / leave)

`Channel`: `index`=1 · `settings`=2 ChannelSettings · `role`=3
(`DISABLED`=0, `PRIMARY`=1, `SECONDARY`=2). Eight slots, 0 is primary.

`ChannelSettings`: `psk`=2 bytes · `name`=3 (< 12 bytes) · `id`=4 ·
`uplink_enabled`=5 · `downlink_enabled`=6 · `module_settings`=7
(`position_precision`=1, `is_muted`=2).

PSK rules: 0 bytes = no encryption; 16 or 32 bytes = AES key; 1 byte is a
shorthand for the well-known default key (`1`) or that key with 1–9 added to
its last byte (`2`–`10`, "simple1"…). Channels using the default/simple keys
are public in practice.

Share URLs: `https://meshtastic.org/e/#<base64url ChannelSet, no padding>`
(the add-only form uses `/?add=true#`). `ChannelSet`: `settings`=1 repeated
ChannelSettings · `lora_config`=2.

**We only ever use "add" semantics.** The reference client's full `setURL`
also overwrites every slot and applies the URL's `lora_config` (modem preset,
region, frequency) to the node. Mesh Messenger never changes radio settings:
it adds the URL's channels to free `DISABLED` slots as `SECONDARY`, skips names
that already exist, ignores `lora_config`, and warns if the URL's preset
differs from the node's.

Writing a channel is an admin message to our own node:
`Data{ portnum=6, want_response=true, payload=AdminMessage }` sent to our own
node number. `AdminMessage`: `set_channel`=33 Channel ·
`get_channel_request`=1 (index + 1) · `get_config_request`=5 ·
`session_passkey`=101 bytes. The reference client first obtains a session
passkey (`get_config_request = SESSIONKEY_CONFIG` (8); the key comes back in
the response's `session_passkey`) and includes it in later admin messages.
Leaving a channel = `set_channel` with `role = DISABLED`.

## Signal and network health

- Per message: `rx_snr`, `rx_rssi`, hops, `via_mqtt`.
- Per node (node DB): `snr`, `hops_away`, `last_heard`, battery/voltage from
  `device_metrics`.
- Our own node: the node broadcasts `Telemetry` on `TELEMETRY_APP`.
  `Telemetry`: `time`=1 · oneof `device_metrics`=2 · `local_stats`=6.
  `LocalStats`: `uptime_seconds`=1, `channel_utilization`=2, `air_util_tx`=3,
  `num_packets_tx`=4, `num_packets_rx`=5, `num_packets_rx_bad`=6,
  `num_online_nodes`=7, `num_total_nodes`=8, `num_rx_dupe`=9,
  `num_tx_relay`=10, `num_tx_dropped`=14, `noise_floor`=15.
- Neighbour info (`NEIGHBORINFO_APP`, only if that module is enabled on the
  mesh): `NeighborInfo{ node_id=1, neighbors=4 }`, `Neighbor{ node_id=1,
  snr=2 float, last_rx_time=3 }`.

## Open questions (verify on hardware)

- Behaviour with more than one TCP client at once, and whether enabling Wi-Fi
  disables Bluetooth on the Heltec V4 (it does on ESP32 boards per Meshtastic
  docs) — affects whether the phone app can be used alongside Zeus.
- Whether admin `set_channel` to the local node over TCP requires the session
  passkey on current firmware.
