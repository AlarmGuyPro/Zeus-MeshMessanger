# Mesh Gateway (planned, optional)

A small always-on service, typically on a Raspberry Pi, that owns the mesh
nodes and buffers every message, so nothing is lost while Zeus is closed or a
node reboots. Built **after** the direct Wi-Fi connectors work.

Status: design only.

## Why

| | Direct (Zeus ↔ node over Wi-Fi) | Gateway (Zeus ↔ Pi ↔ node) |
|---|---|---|
| Messages held while Zeus is closed | Node RAM: MeshCore 256, Meshtastic 8–32 packets | Thousands, on disk |
| Survives node reboot / power cut | No | Yes (already collected) |
| MeshCore firmware | Custom build with Wi-Fi credentials | Stock USB companion firmware |
| Node connection | Node's Wi-Fi | USB serial (also powers the node) |
| Extra hardware | None | Pi (3/4/5/Zero 2 W, 64-bit OS) |

## Shape

```
Zeus ── MeshMessenger plugin ── Gateway connector ──HTTP (LAN, token)──▶ mesh-gateway (Pi)
                                                                           ├─ MeshCore node  (USB)
                                                                           └─ Meshtastic node (USB or Wi-Fi)
```

- **Same protocol code.** `src/MeshMessenger.Mesh` (connectors, routing,
  transmit coordinator) has no Zeus dependency. The plugin and the gateway
  both host it. The plugin's third connector type, `Gateway`, talks to the
  gateway instead of a node.
- **The gateway owns the nodes.** It keeps the node connections open 24/7,
  drains the nodes' queues as messages arrive, re-logs into rooms, and stores
  everything.
- **Same rules.** Conversations are bound to one node; replies are routed by
  conversation; no fallback; one transmission at a time across nodes;
  outbound messages fail rather than wait (no "send later" unless the
  operator explicitly asks for it in a future version).

## Gateway API (sketch)

All requests carry `Authorization: Bearer <token>`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/status` | Gateway version, nodes and their state |
| GET | `/v1/events?since=<cursor>&wait=25` | Messages, delivery updates, node changes after a cursor; long-polls up to 25 s |
| GET | `/v1/conversations` | Summaries |
| POST | `/v1/conversations/{id}/reply` | Reply on the conversation's own node |
| POST | `/v1/conversations` | Start a conversation on an explicit node |
| POST | `/v1/tools/...` | Traceroute, path discovery, channels, rooms, status (operator-initiated) |

The plugin keeps its own copy of history and syncs with a cursor, so a Zeus
restart or a gateway outage loses nothing and duplicates are dropped by id.

## Storage

Append-only, line-delimited JSON log per day under `/var/lib/mesh-gateway/`,
fsync'd per write, plus an in-memory index rebuilt at start. Retention by
count and age (default: 10,000 messages or 90 days). No database package:
the repository stays free of third-party NuGet packages (see
ZEUS-REQUIREMENTS.md §4), and this is plenty for thousands of messages.
Recommend a USB SSD or a good-quality SD card; writes are small and
infrequent.

## Install and manage

Goal: one command to install, one page to manage, nothing to compile.

**Install** (Raspberry Pi OS 64-bit, with the node plugged in by USB):

```bash
curl -fsSL https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/latest/download/install-gateway.sh | sudo bash
```

The script:

1. checks the OS/architecture (arm64 or x64 Linux);
2. downloads the self-contained gateway build (no .NET install needed) and
   verifies its SHA-256 from the release;
3. creates a `meshgw` system user, adds it to the `dialout` group for USB
   serial;
4. installs to `/opt/mesh-gateway`, config in `/etc/mesh-gateway/`, data in
   `/var/lib/mesh-gateway/`;
5. installs and starts a `systemd` service (restarts on failure, starts at
   boot);
6. prints the address of the gateway page and a one-time **pairing code**.

**Manage**:

- **Web page** at `http://<pi>:8650` (LAN only): node status, detected USB
  nodes and their network, add/remove nodes, storage use, recent log lines,
  pairing codes, update button.
- **Command line**: `mesh-gateway status | logs | pair | update | backup |
  uninstall`.
- **Updates** are manual and explicit (`mesh-gateway update` or the page's
  button), download the release, verify the checksum, and keep the previous
  version for rollback. No automatic updates.
- **Pairing with Zeus**: in Mesh Messenger, *Add node → Gateway*, enter the
  Pi's address and the pairing code; the gateway returns a long-lived token
  that the plugin stores. Tokens can be revoked from the gateway page.
- **Discovery** (nice to have): the gateway announces itself over mDNS
  (`_mesh-gateway._tcp`) so Zeus can offer it in a list.

## Security

- LAN only by default; binds to the local network, never needs port
  forwarding. The gateway page warns if it detects a public address.
- Every API call needs the token; sending is a transmit, so an
  unauthenticated LAN device must never be able to trigger one.
- Channel keys and room passwords are stored on the gateway with file
  permissions limited to the service user; never logged.
- The gateway makes no outbound internet connections except an explicit
  update.

## Repository layout

```
src/MeshMessenger.Mesh/      shared library: core + connectors (no Zeus, no ASP.NET)
src/MeshMessenger/           Zeus plugin (entrypoint), references the library
src/MeshGateway/             gateway service (future), references the library
tests/MeshMessenger.Tests/   console test runner, no packages (future)
web/                         plugin UI
deploy/gateway/              install script, systemd unit (future)
```

Only `src/MeshMessenger` is the Zeus `dotnet.project`; the gateway ships as
separate GitHub release assets and is never inside the Zeus package.

## Open questions

- USB serial framing for each firmware (MeshCore uses the same `<`/`>` frame
  header over serial; Meshtastic uses the same `0x94 0xC3` stream framing)
  — confirm on hardware.
- Whether to support a Meshtastic node on Wi-Fi behind the gateway, or USB
  only.
