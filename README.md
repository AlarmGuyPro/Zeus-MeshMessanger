# Mesh Messenger for Zeus SDR

Read, send and reply to **Meshtastic** and **MeshCore** text messages from
inside Zeus, through LoRa nodes (such as a Heltec V4) on your Wi-Fi.

> Status: early development. The panel, inbox and routing work; the two node
> connectors are not implemented yet, so nodes show as disconnected and sends
> fail with "not implemented".

## How it behaves

- One inbox for both networks. Every conversation shows a network badge
  (**MT** = Meshtastic, **MC** = MeshCore) and the node it belongs to.
- **A reply always goes out on the node and network the conversation came in
  on.** If that node is offline, the reply fails and is marked "Not sent". It
  is never re-sent on the other network.
- Starting a new message asks which node to send from.

## What it accesses

- **Network:** TCP connections to the mesh nodes you configure on your LAN.
- **Settings:** its own node list, in Zeus's per-feature settings store.

It does not read or change radio state, never keys the transmitter, and does
not touch PureSignal.

## Setting up a node

Flash the node with Wi-Fi-enabled firmware and join it to the same network as
the Zeus computer:

- **Meshtastic:** enable Wi-Fi in the node's network settings. Zeus connects to
  TCP port 4403.
- **MeshCore:** use a companion-radio Wi-Fi build. Zeus connects to its TCP
  port (5000 by default).

Give the node a fixed IP address (a DHCP reservation in your router) so Zeus
can find it after a restart.

## Building (developers)

Requirements: .NET 10 SDK (the exact version in `global.json`), PowerShell 7,
Node.js 22+.

```powershell
pwsh scripts/build-package.ps1
```

This builds the panel (`web/`), the backend (`src/MeshMessenger/`) and writes
`artifacts/io.github.alarmguypro.meshmessenger/io.github.alarmguypro.meshmessenger-<version>.zip`.
Install it in Zeus with **Features → Community → Install local feature**.

The version is set in two places that must match: `src/MeshMessenger/plugin.json`
and `<Version>` in `src/MeshMessenger/MeshMessenger.csproj`.

See [DESIGN.md](DESIGN.md) for the architecture and routing rules.

## License

GPL-2.0-or-later. See [LICENSE](LICENSE). `sdk/` is a vendored copy of the
public Zeus plugin contracts (see `sdk/UPSTREAM.md`).
