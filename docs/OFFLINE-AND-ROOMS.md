# When Zeus is offline, and MeshCore room servers

How messages reach the station when Zeus isn't running, what is lost and
when, and how a MeshCore room server fits in. Facts here come from the
firmware sources listed in [protocols/](protocols/); room server and offline
queue details are from `meshcore-dev/meshcore` @ `a366955`
(`examples/simple_room_server`, `examples/simple_repeater`,
`examples/companion_radio`) and Meshtastic `firmware` `develop`
(`src/mesh/mesh-pb-constants.h`).

## The intended setup

A **dedicated node for Zeus**: one LoRa node (e.g. a Heltec V4) on the shack's
Wi-Fi, mains powered, that nothing else connects to. It is its own station on
the mesh, with its own name, contacts and DMs, reaching the rest of the mesh
through local repeaters like any other companion.

```
  phone / handheld ──┐
  other companions ──┼── tower repeater ──┬── Zeus node (Wi-Fi) ── Zeus PC
                     │                    └── room server (optional)
```

Consequences:

- Channels: Zeus sees the same group traffic as your other devices once the
  Zeus node has joined the same channels.
- DMs: a DM to your handheld stays on your handheld. People DM the Zeus node
  by its own name (pick one like `<callsign> Shack`; on MeshCore a shorter
  name also leaves more room in channel messages).
- The node runs 24/7 even when the Zeus PC is off. That is what makes most of
  "Zeus offline" work.

## Three different kinds of "offline"

### 1. Zeus closed, Zeus node still powered (the common case)

The node keeps receiving and holds messages until Zeus connects again.

| | MeshCore companion | Meshtastic |
|---|---|---|
| What's held | Every received DM, channel message and room post | Every received packet of any kind (text, position, telemetry, node info…) |
| How many | 256 frames on the Heltec V4 Wi-Fi build (16 on generic builds) | 8–32 packets depending on the board's memory class |
| When full | Oldest **channel** message is dropped first; DMs are kept | New packets are dropped |
| DM sender sees | Delivered (the node acks on receipt) | Delivered (the node acks on receipt) |
| Survives node reboot / power cut | No (held in RAM) | No |

On connect, Mesh Messenger drains the queue (MeshCore:
`SYNC_NEXT_MESSAGE` until "no more"; Meshtastic: the backlog arrives after the
config handshake) and shows those messages with their original timestamps,
marked **"received while Zeus was closed"**.

So for MeshCore, a dedicated node covers days of normal traffic. For
Meshtastic the node-side buffer is small and shared with telemetry, so on a
busy mesh text can be lost within minutes. Mitigations, best first:

1. Leave Zeus running (Zeus is often on anyway in a shack).
2. Use Meshtastic **Store & Forward** if the mesh has a server node: on
   connect, Mesh Messenger asks it for history (`STORE_FORWARD_APP` = 65,
   `CLIENT_HISTORY`), and de-duplicates by packet id. A Store & Forward server
   needs an ESP32 with PSRAM and is set up in the native apps.
3. Accept the gap and show it: if the connector sees that the node dropped
   packets, or the outage was long, the inbox shows "messages may have been
   missed between <time> and <time>".

### 2. Zeus node off or out of range (power cut, firmware flash, moved)

Nothing on the node itself can help.

- **DMs to the Zeus node** fail: the sender's app shows "not delivered" after
  its retries. Nothing is stored anywhere.
- **Channel messages** are simply not heard by the Zeus node. MeshCore has no
  store-and-forward for channels.
- **Room posts** are the exception: a room server keeps them and pushes the
  missed ones when the Zeus node logs back in (below).
- Meshtastic: Store & Forward (as above) can also replay channel traffic.

### 3. Zeus restarts (PC reboot, plugin update)

This must never lose history the operator has already seen. Mesh Messenger
keeps its own message history in the per-feature settings store (no extra
filesystem permission needed), so a restart shows everything from before,
plus whatever the node queued meanwhile. This moves **persistence earlier**
in the plan: it is needed before the connectors are useful, not after.

## MeshCore room servers

A room server is a MeshCore node running the room-server firmware. It acts as
a small bulletin board: logged-in members post, and the server pushes every
post to every member it hasn't reached yet.

How it works (from the firmware):

- **Joining** is a login with the room's password (members get read/write;
  the room can optionally allow read-only guests without the password). Up to
  20 members.
- **Posts** are up to 151 bytes. The server keeps the **last 32 posts**, in
  RAM — a room server reboot empties it.
- For each member the server tracks "synced up to" (a timestamp). It pushes
  newer posts one at a time to each member, waiting for an ack. Posts are
  never pushed back to their author.
- **After 3 failed pushes to a member, the server stops trying** until that
  member logs in again. The member's node remembers its own "synced up to"
  per room and sends it with every login, so the next login receives exactly
  the posts it missed (up to 32).
- A pushed post arrives at the companion as a signed DM from the room, with
  the author's key prefix; the companion's contact list turns that into a
  name.
- A room server also forwards packets like a repeater (unless forwarding is
  turned off), so it can double as one.

### What a room gives the station

It is the "leave a message for the shack" place that works even when the Zeus
node itself was off: anyone in the room posts, and the Zeus node collects the
posts next time it logs in. It is also shared — every member sees the thread,
which suits club or family use. It is not private mail: every member reads
every post.

### Where to put it

- **At the house, mains powered** (simplest): a second small node next to, but
  not connected to, the Zeus node. Posts are lost only when that node loses
  power.
- **Reflash the tower repeater as a room server**: it keeps repeating and
  serves the room from the best site, but a solar brown-out empties the room,
  and it is a firmware change on your most important node.
- Either way the room server is set up (name, room password, read-only policy)
  with the native tools; Mesh Messenger only joins it.

### How Mesh Messenger handles rooms

- A room is a third conversation kind alongside channel and direct:
  `(connector, room, <room key>)`. It belongs to the MeshCore node like any
  other conversation, so DESIGN.md's routing rules apply unchanged.
- **Join a room**: pick a room-server contact (from adverts), enter the room
  password once. The password is stored in the feature's settings, never
  shown again or logged. This is a member login, not an admin login; admin
  passwords are never asked for or stored.
- **On every connect** Mesh Messenger logs in to each joined room so the
  server resumes pushing (it gave up after 3 failures while we were away),
  then shows the missed posts as they arrive.
- **Posting** sends a DM to the room; the post appears in our own history
  immediately because the room never echoes it back.
- The conversation shows each post's author, and a banner if the login failed
  (wrong password, room unreachable).
- Logins are flood-routed the first time, so they are spaced out and never
  retried in a tight loop.

## Repeater status (your tower repeater)

Reading a repeater's status needs a **guest** login to it; status is open to
guests. If the repeater's guest password is blank, Zeus logs in with an empty
password; otherwise the operator enters the guest password once. The admin
password is never used.

Status returned: battery mV, TX queue length, noise floor, last RSSI and SNR,
packets received/sent (flood and direct), duplicates, airtime TX/RX, uptime,
error count. This goes on the **Health** tab next to the Zeus node's own
figures, refreshed only on request (or on a slow, operator-chosen timer).

## Open questions

- Whether Meshtastic delivers its to-phone queue to a TCP client that
  reconnects, or only to the client that was connected when it filled (test
  on hardware).
- Whether the plugin install folder survives feature updates; until known,
  history lives in the settings store, not files.
- Default repeater guest password on current firmware (believed blank).
