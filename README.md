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

Give Zeus its own node: a LoRa board (such as a Heltec V4) on your Wi-Fi,
powered from the shack, that nothing else connects to. It becomes its own
station on your mesh, reaching it through your local repeaters, and it keeps
collecting messages while Zeus is closed. People message the station by the
node's name, so pick a clear one such as `<callsign> Shack`.

Keep the node and its antenna away from your HF antenna and feedline: strong
HF transmit signals can overload or damage it, and its Wi-Fi and switching
noise can show up in the SDR. An external antenna mounted a little way from
the station helps both ways.


Flash the node with Wi-Fi-enabled firmware and join it to the same network as
the Zeus computer:

- **Meshtastic:** enable Wi-Fi in the node's network settings. Zeus connects to
  TCP port 4403.
- **MeshCore:** use the companion-radio Wi-Fi build for your board (for the
  Heltec V4: `heltec_v4_companion_radio_wifi`). MeshCore's Wi-Fi network name
  and password are set when the firmware is built, so you need a build made
  with your network's details. Zeus connects to TCP port 5000. A MeshCore node
  talks to one app at a time: connecting the phone app or meshcore-cli over
  Wi-Fi disconnects Zeus until you close them.

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
