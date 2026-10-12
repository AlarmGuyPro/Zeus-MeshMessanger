# Pull request body — Mesh Messenger 0.4.1

Catalog PR title:

```text
feat(registry): add io.github.alarmguypro.meshmessenger 0.4.1
```

---

## What this is

Mesh Messenger puts Meshtastic and MeshCore traffic in one Zeus panel. The
station keeps one or two LoRa nodes on Wi-Fi; Zeus reads their messages, shows
them in a single inbox, and sends replies back out through the node a
conversation arrived on.

The rule the whole design hangs on: **a reply always goes out on the network
and node its conversation came from.** A MeshCore conversation is never
answered over Meshtastic, or the other way round. If that node is offline the
reply fails and says so, rather than silently finding another route.

## Identity

| | |
|---|---|
| Feature ID | `io.github.alarmguypro.meshmessenger` |
| Version | 0.4.1 |
| SDK | ABI 1, minimum 1.5.0 |
| Platforms | `any` — fully managed, no native or platform-specific files |
| License | GPL-3.0-or-later (feature-owned `LICENSE` in the package) |
| Source | https://github.com/AlarmGuyPro/Zeus-MeshMessanger |
| Commit | `5b90ba0a321d477a6282f24900d2ab686fe6f233` |
| Intake ZIP | https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v0.4.1/io.github.alarmguypro.meshmessenger-0.4.1.zip |
| SHA-256 | `a854b549ce4717da09a16558cf533584771ae9c60649347de2c9b63ab28c53fc` |

## Capabilities and permissions, and why

Declared: `NetworkAccess`, `PersistSettings`. Permissions: `network: true`,
`fileSystemRead: false`, `fileSystemWrite: false`.

**Network.** The feature's entire purpose is talking to LoRa nodes over the
local network: MeshCore's companion protocol on TCP 5000, Meshtastic's on TCP
4403. It connects only to nodes the operator configured, or to ones found by an
explicit Scan. **Public IP addresses are refused** — `HostValidator` accepts
only private ranges, and loopback only under a test flag that is never set in
the shipped package. Discovery is mDNS plus a probe of 4403/5000 across private
ranges, identity-confirmed, and it runs only on Scan, at setup, or when
re-finding a node that moved. There are no internet calls, no update checks and
no telemetry of any kind.

**Persisted settings.** Node addresses and identities, message history,
per-conversation mute flags, and channel keys. These go in the plugin settings
store, which is why there is no filesystem permission: the feature calls no
`File` or `Directory` API at all.

**Not used:** no `[FromServices]`, no `HttpContext.RequestServices`, no
environment variables, no reflection, no `HostDataDirectory`, no dynamic code
loading. PureSignal is never touched, and the feature never keys a
transmitter.

## Transmit discipline

This is a radio feature, so every control in the UI is labelled with whether
pressing it puts RF on the air — "Transmits: no" in grey, "Transmits: yes" in
amber. In 0.4.1 exactly one control transmits: **Send advert** on the Nodes
tab, which announces the operator's own node so other stations can add it.
Everything else — reading contacts, joining a channel, tidying the contact
list, muting, deleting history — is either local to Zeus or a local command to
the node over Wi-Fi.

Sending a message transmits, obviously, and only ever because the operator
pressed Send. Transmissions across nodes are serialised and spaced by a
coordinator so two nodes don't talk over each other.

The feature never changes a node's radio settings.

## UI styling contract

- Everything is scoped under the feature root class
  `io-github-alarmguypro-meshmessenger`, with prefixed secondary names.
- Colors come only from the public token subset. There is no raw hex, rgb, hsl
  or named color anywhere in the feature CSS.
- State colors are used semantically: `--tx` only for transmit, `--amber` for
  warnings, `--ok` for confirmed healthy, `--accent` for selection and focus.
  No state is signalled by color alone — each carries a word or a symbol too.
- Dark and light themes, normal and narrow widths, and 200% scaling are all in
  the attached screenshots.
- Loading, empty, error, disconnected and unavailable states are all explicit
  and are all shown.
- Keyboard focus stays visible; every control has an accessible name; no action
  is hover-only.
- All backend work goes through `callBackend`. Polling timers are cleared on
  unmount.

## Screenshots

Attached, 21 images:

| | |
|---|---|
| 01–05 | Dark theme: inbox, a selected direct thread with a failed send, the Nodes tab, Settings, Debug |
| 06–08 | Light theme: inbox, a channel thread, Nodes |
| 09–10 | Narrow width |
| 11–12 | 200% display scaling |
| 13–14 | Visible keyboard focus |
| 15 | Loading |
| 16 | Backend error |
| 17–18 | A node that can't be reached, on Nodes and Debug |
| 19–21 | Empty states with no nodes configured |

Screenshot 17 is the densest piece of review evidence: a disconnected node in
the status bar, the amber full-contact-list warning, the amber "Transmits: yes"
label beside Send advert, and the channel and contact management below it.

## Local checks

All run from the catalog repository on Linux against the exact submitted ZIP
and a fresh clone of commit `5b90ba0`:

| Check | Result |
|---|---|
| `dotnet build Zeus.CommunityFeatures.slnx -c Release` | 0 warnings, 0 errors |
| `ajv validate` — registry, template manifest, and this feature's `zeus-build.json` | all valid |
| `tools/validate-sdk-boundary.ps1` | 21 GPL public-contract SDK files validated |
| `tools/test-package-validator.ps1` | path-safety regression tests passed |
| `tools/validate-registry.ps1` | 23 plugins, 41 versions validated |
| `tools/validate-package.ps1` with id, version, ABI, min version and schema | exit 0 |
| `templates/hello-world/build-package.ps1` + validate | exit 0 |
| `tools/verify-source-build.ps1` from a clean clone | **clear** |
| Package security scan (`PackageSecurityScan scan`) | **CLEAR — 0 fail, 0 review, 2 info** |
| Feature's own test suite | 32/32 passed |

The two info findings are the expected ones: the socket and networking types
the connectors use, and the manifest capability disclosure.

The package ships a **single assembly**. The mesh protocol code lives under
`src/MeshMessenger/Mesh/` inside the plugin project rather than in a project of
its own, so there is no second DLL for a reviewer to chase.

## Provenance

All code in this feature is original work for it, except:

- `src/MeshMessenger/Mesh/Discovery/` (`Mdns.cs`, `Ipv4Network.cs`) — original
  implementations written against the published mDNS and IPv4 specifications,
  listed in `THIRD_PARTY_NOTICES.md`.
- `sdk/Zeussdr.Zeus.Plugins.Contracts/` — the vendored public SDK snapshot,
  unmodified.

The MeshCore and Meshtastic wire formats were implemented from each project's
public firmware source and protocol documentation. No Zeus private source,
host implementation detail, DSP or radio protocol logic was read, copied or
relied on.

## Testing

Tested against real hardware as well as the simulated nodes in the test suite:
two Heltec V4 boards, one running MeshCore companion Wi-Fi firmware v1.17.1 and
one running Meshtastic 2.7.26, both on Wi-Fi alongside a Windows Zeus host.
Direct messages, channel traffic, contact management and channel join/leave
were exercised on the air. Node discovery over mDNS was confirmed to find the
Meshtastic node without a typed address.
