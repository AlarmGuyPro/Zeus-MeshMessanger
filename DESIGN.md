# Mesh Messenger — design

A Zeus SDR community feature that shows, sends and replies to Meshtastic and
MeshCore text messages through LoRa nodes (Heltec V4 or similar) that are on
the station's Wi-Fi.

Feature ID (permanent): `io.github.alarmguypro.meshmessenger`

## Deployment model

Zeus gets a **dedicated node**: one LoRa node on the shack's Wi-Fi, mains
powered, used by nothing else. It is its own station on the mesh (own name,
contacts and DMs) and reaches the rest of the mesh through local repeaters.
Because it stays powered when Zeus is closed, it holds incoming messages until
Zeus reconnects. See [docs/OFFLINE-AND-ROOMS.md](docs/OFFLINE-AND-ROOMS.md)
for offline behaviour and MeshCore room servers.

## Shape

One feature, one panel, one unified inbox, and one **connector** per
configured node. A connector speaks exactly one protocol:

| Connector | Transport | Text limit |
|---|---|---|
| Meshtastic | Firmware TCP API, port 4403, framed protobuf `ToRadio`/`FromRadio` | 200 bytes (protocol max 233) |
| MeshCore | Companion-radio Wi-Fi firmware, TCP port 5000, companion frame protocol | DM 160 bytes; channel 160 − (our node name + 2) bytes |

Protocol references, with upstream commits: [docs/protocols/meshtastic.md](docs/protocols/meshtastic.md),
[docs/protocols/meshcore.md](docs/protocols/meshcore.md). Feature scope:
[docs/FEATURES.md](docs/FEATURES.md).

The operator can configure zero, one or several nodes of either type
(typically one of each).

```
Zeus panel (web/)  ──callBackend──▶  Endpoints  ──▶  MessageRouter ──▶ connector (one per node) ──TCP──▶ node
                                       │                 │
                                       └──▶ MessageStore ◀┘ (inbound messages, filed by conversation key)
```

## Code layout

- `src/MeshMessenger.Mesh/` — conversation model, routing, store and the
  Meshtastic / MeshCore connectors. No Zeus or ASP.NET dependency, so the
  optional Pi gateway ([docs/GATEWAY.md](docs/GATEWAY.md)) can reuse it.
- `src/MeshMessenger/` — the Zeus plugin: lifecycle, settings, HTTP endpoints.
- `web/` — the panel.

Zeus-specific rules this design follows (transmit safety, in-process
stability, LAN-only networking, packaging, licensing) are collected in
[docs/ZEUS-REQUIREMENTS.md](docs/ZEUS-REQUIREMENTS.md).

## Rule 1: a conversation belongs to exactly one node

Every conversation is identified by a `ConversationKey`:

```
(connectorId, kind = channel | direct, peer)   →   id "meshtastic-1:dm:!a1b2c3d4"
```

`connectorId` identifies the configured node, and through it the network.
The same channel name or the same person seen on Meshtastic and on MeshCore
produces **two** conversations. They are never merged.

## Rule 2: replies are addressed by conversation, never by network

`POST conversations/{id}/reply` takes only the text. `MessageRouter` looks the
connector up from the conversation key. There is no API that lets the panel
say "send this reply on network X", so a UI bug cannot misroute a reply.

## Rule 3: no fallback, ever

If a conversation's node is disconnected, errored or removed from
configuration, the reply **fails** and is shown as "Not sent" with the reason.
It is never re-sent on another node or network. Connectors cannot see each
other, so they cannot hand off either.

## Rule 4: only a new conversation chooses its node

`POST conversations` requires an explicit `connectorId`. With more than one
node configured the panel preselects nothing; the operator picks. After the
first send the conversation is bound to that node like any other.

## Rule 5: inbound messages are filed under the connector that received them

Connectors publish through `MeshConnectorBase.PublishInbound`, which always
uses the connector's own id. The router also drops any inbound message whose
key names a different connector (defence in depth).

## Rule 6: origin is always visible

Every conversation and node shows a text network badge (`MT` / `MC`, with the
full name as its accessible label) and the node name. The composer says
"Reply via <node> (<network>)". Color is never the only signal.

## Backend API

All under `/api/plugins/io.github.alarmguypro.meshmessenger/`:

| Method | Path | Purpose |
|---|---|---|
| GET | `status` | Configured nodes and connection state |
| GET | `conversations` | Inbox summaries (newest first, unread counts) |
| GET | `conversations/{id}/messages` | Thread; marks it read |
| POST | `conversations/{id}/reply` | `{ text }` — reply on the conversation's own node |
| POST | `conversations` | `{ connectorId, kind, peer, text }` — start a conversation |

A failed send (offline node, too long) returns 200 with `ok: false` and an
`error`, so the panel can show the reason beside the message.

The panel polls every 3 seconds. Push (SSE/WebSocket) is not used: ABI 1 UI
code must go through `callBackend`, and direct `EventSource`/`WebSocket` use is
flagged by the catalog's security scan.

## Zeus constraints that shape the code

- **No third-party NuGet packages.** The catalog's assembly scan only allows
  references to `System.*`, `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`
  and `Zeus.Plugins.Contracts` unless the DLL ships in the package, and the
  rebuild check rejects packages that carry build-time code. The Meshtastic
  protobuf and MeshCore frame codecs are therefore small hand-written
  encoders/decoders for only the messages we use.
- **Capabilities:** `NetworkAccess` (TCP to the nodes on the LAN) and
  `PersistSettings`. Nothing else: no radio state, no radio control, no audio,
  no filesystem. This feature never touches the transmitter or PureSignal.
- **Rebuild contract:** `zeus-build.json` + `global.json` at the root. The
  browser build lives in `web/`, outside the .NET project folder, as the
  rebuild check requires; `scripts/build-package.ps1` packages from the same
  contract so local ZIPs and CI rebuilds match.
- **Line endings:** `.gitattributes` forces LF so a Windows checkout packages
  the same bytes the Linux rebuild produces.
- **UI:** one ESM module, React externalized, all CSS scoped under
  `.io-github-alarmguypro-meshmessenger` and using only Zeus tokens. Keys typed
  into the panel's fields are stopped from reaching host hotkeys (Space is
  transmit in Zeus).

## Transmit coordination

Everything Mesh Messenger transmits goes through one transmit coordinator,
shared by all connectors: one operator-initiated transmission at a time
across every node, spaced by the previous send's airtime. There is no
interlock with Zeus's own transmitter (decided; see ZEUS-REQUIREMENTS.md §1).
Nothing transmits without an operator action except the opt-in, off-by-default
automatics listed in [docs/help/options.md](docs/help/options.md).

## Connection etiquette

- **MeshCore nodes accept one TCP client at a time** and drop the old one
  when a new one connects. The connector reconnects with backoff and reports
  "another client took the node" instead of fighting the operator's phone or
  meshcore-cli.
- Anything that transmits beyond a message (traceroute, path discovery,
  trace, advert) only happens on an explicit operator action, with a cooldown.

## Milestones

1. **Scaffold** (this commit): routing core, in-memory store, endpoints,
   connector stubs that report "not implemented", panel with every state.
2. **Node configuration**: `GET/PUT config` endpoints and a settings view in the
   panel (add/edit/remove nodes; host, port, network, name). Restart connectors
   on change.
3. **Persistence**: message history in the settings store, so a Zeus restart
   never loses what was already shown (moved up: needed before the connectors
   are useful).
4. **Transmit coordinator** and the test runner (plain console project, no
   packages): routing, no-fallback, coordinator and text-limit tests. Comes
   before the connectors because every send goes through it.
5. **MeshCore connector**: companion TCP framing, app start, clock sync,
   contact sync, drain the offline queue, channel and contact messages,
   reconnect with backoff.
6. **Meshtastic connector**: TCP framing, `want_config` handshake, node DB for
   names, text send/receive on channels and DMs, reconnect with backoff,
   Store & Forward history where a server exists.
7. Then the tools in docs/FEATURES.md, tier by tier, including room servers
   and repeater status.
8. **Release**: codec tests from captured frames, screenshot set, first
   `v0.x` release and catalog PR.
9. **Gateway** (optional, separate install): see docs/GATEWAY.md.

## Open questions

- Confirm Zeus already ignores its hotkeys while focus is in a text field; if
  so, the panel's key isolation is belt and braces.
