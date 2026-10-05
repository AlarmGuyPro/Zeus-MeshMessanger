# Mesh Messenger — feature scope

What belongs inside Zeus: the things an operator checks or does many times a
session, while sitting at the radio. Everything else stays in the official
Meshtastic / MeshCore apps and flashers.

Protocol details for each item are in [protocols/meshtastic.md](protocols/meshtastic.md)
and [protocols/meshcore.md](protocols/meshcore.md).

Guiding rules:

- **Read freely, write narrowly.** Mesh Messenger may change only what an
  operator changes routinely: messages, channel membership, contact routes,
  favourites.
  It never changes radio parameters, device role, keys, firmware or modules.
- **Every action stays on its own node** (DESIGN.md rule 1–3 apply to tools
  too: a traceroute on a Meshtastic conversation runs on that Meshtastic node).
- **Airtime is shared.** Anything that transmits beyond a message (traces,
  path discovery, adverts, status requests) is an explicit button press, shows
  a cooldown, and is never automatic or repeating.

## Panel layout

One panel with four tabs: **Inbox**, **Channels**, **Nodes**, **Health**. The
node bar across the top (network badge, name, connection state, battery) is
always visible.

## Tier 1 — messaging (table stakes)

| Tool | Meshtastic | MeshCore |
|---|---|---|
| Unified inbox, channel + direct conversations | `TEXT_MESSAGE_APP` | channel / contact messages |
| Send, reply, start a conversation | ✓ | ✓ |
| Correct per-network length limit, enforced before sending | 200 bytes | DM 160 B; channel 160 − (our name + 2) B — firmware truncates silently, so we must check |
| Delivery status per message | Sent → Acked / failed reason (`Routing` error) | Sent (flood/direct) → Delivered + round-trip ms (DMs only; channels have no ack) |
| Per-message reception line: SNR, RSSI, hops, via MQTT | SNR, RSSI, hops = `hop_start − hop_limit`, MQTT flag | SNR, hops (or "direct") |
| Catch up on messages received while Zeus was away | node's queue on reconnect | drain the offline queue on connect |
| Device clock sync on connect | — | `SET_DEVICE_TIME` (automatic, required for sane timestamps) |
| Unread counts, mute a conversation (local only) | ✓ | ✓ |

## Tier 2 — operational tools (used all the time)

### Channels / groups

| Tool | Meshtastic | MeshCore |
|---|---|---|
| List channels with type (public / hashtag / private) and slot | 8 slots | up to 40 slots on the V4 Wi-Fi build |
| **Join public group** | default/simple-key channels, or a share URL | public channel, or `#hashtag` (key derived from name) |
| **Join private group** | paste share URL (`meshtastic.org/e/#…`) — add-only, radio settings in the URL ignored, warn if preset differs | paste name + 16-byte secret (hex) |
| **Create private group** | random 32-byte key → show share URL + QR | random 16-byte secret → show name + secret + QR |
| Share an existing channel | URL + QR | name + secret + QR |
| Leave a channel | set slot `DISABLED` (confirm) | blank the slot (confirm) |

Channel keys are shown only on an explicit "Show key" action and never logged.

### Nodes / contacts

| Tool | Meshtastic | MeshCore |
|---|---|---|
| Heard-node list: name, short name, last heard, SNR, hops away, battery | node DB (`NodeInfo`) | contacts + adverts |
| Node type | role from `User` | chat / repeater / room / sensor |
| Favourite / ignore | admin `set_favorite_node` / `set_ignored_node` | local only (v1) |
| Start a DM from a node | ✓ | ✓ |
| **Distance and bearing** from the station, using the operator grid Zeus already knows (`OperatorIdentity`; approximate to grid-square precision) | from `Position` | from contact lat/lon |
| Announce ourselves | — (firmware handles NodeInfo) | Send advert: zero-hop or flood |

### Route and signal

| Tool | Meshtastic | MeshCore |
|---|---|---|
| **Traceroute** to a node: hop list with SNR each way | `TRACEROUTE_APP` (rate-limited) | path discovery (route each way), then **trace** for per-hop SNR |
| Current route to a contact | `hops_away` | stored out-path (repeater hops) |
| Reset route (force re-learn by flood) | — | `RESET_PATH` |
| Message route detail: hops, SNR/RSSI of last hop | ✓ | ✓ |

### Health (own node)

| Tool | Meshtastic | MeshCore |
|---|---|---|
| Battery / voltage, uptime | `DeviceMetrics` | `GET_STATS` core |
| **Channel utilisation / airtime** | `channel_utilization`, `air_util_tx` | TX/RX airtime |
| **Noise floor**, last RSSI/SNR | `LocalStats.noise_floor` | `GET_STATS` radio |
| Packet counters (rx, tx, bad, dupes, relayed) | `LocalStats` | `GET_STATS` packets |
| Nodes online / total | `LocalStats` | contact count |
| Radio summary, read-only (region/preset or freq/BW/SF/CR, TX power) | config | `SELF_INFO` |
| Firmware version / model, read-only | `DeviceMetadata` | `DEVICE_INFO` |

## Tier 3 — later, if wanted

- MeshCore **repeater status** (`STATUS_REQ`, read-only, no login).
- Meshtastic **neighbour info** (only where the module is enabled on the mesh).
- Signal history sparkline per node (SNR over the session).
- Reactions/tapbacks and replies-to (`Data.emoji`, `reply_id`).
- Message persistence across Zeus restarts (planned for core, milestone 5).

## Left to the native apps and flashers

Deliberately **not** in Mesh Messenger:

- Firmware updates, OTA/DFU, flashing, bootloaders.
- Wi-Fi provisioning (MeshCore Wi-Fi credentials are compiled into the firmware anyway).
- Radio configuration: region, modem preset, frequency, bandwidth, SF/CR,
  TX power, hop limit.
- Device role, owner name changes, Bluetooth PIN/pairing.
- Keys and security: private-key export/import, admin keys, PKI management.
- Factory reset, node-DB reset, reboot/shutdown.
- Module configuration: MQTT, serial, store-and-forward, telemetry sensors,
  range test, canned messages, external notification.
- Remote administration of other nodes; repeater/room-server login and CLI
  commands; MeshCore flood-scope/region keys; auto-add contact policy.
- Raw packets, signing, custom variables, sensors/telemetry requests to
  sensor nodes.
- Maps beyond distance/bearing (the apps do maps well).

## Build order

1. Node configuration in the panel (milestone 2 in DESIGN.md).
2. Both connectors: connect, handshake, messages, delivery status, reception
   line (tier 1).
3. Channels tab: list, join (public/hashtag/URL/key), create, share, leave.
4. Nodes tab: heard list, distance/bearing, DM from node, advert.
5. Route tools: traceroute / path discovery / trace / reset path.
6. Health tab.
