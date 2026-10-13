# Pull request body — Mesh Messenger 1.0.3

Catalog PR title:

```text
feat(registry): add io.github.alarmguypro.meshmessenger 1.0.3
```

---

## What this is

Mesh Messenger 1.0.3, a new version of a feature already in the catalog
(0.4.1, merged in #19). The entry gains one element in its `versions` array;
the published 0.4.1 object is untouched.

Everything in this version answers the review of #19. Thank you for it — the
mutation run in particular found a real gap, and it is now part of the build.

## Identity

| | |
|---|---|
| Feature ID | `io.github.alarmguypro.meshmessenger` |
| Version | 1.0.3 |
| SDK | ABI 1, minimum 1.5.0 |
| Platforms | `any` — fully managed, no native or platform-specific files |
| License | GPL-3.0-or-later (feature-owned `LICENSE` in the package) |
| Source | https://github.com/AlarmGuyPro/Zeus-MeshMessanger |
| Commit | `8e21064a7f5154ced78006302e0052b9a1ff331d` |
| Intake ZIP | https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v1.0.3/io.github.alarmguypro.meshmessenger-1.0.3.zip |
| SHA-256 | `09a3389462776798f46a995a23f0f79bfbfbb17eeef6d7f8a2beb909a77b68aa` |

## What the review asked for, and what changed

**1. A MeshCore channel index that is not a real slot.** Confirmed exactly as
described: the index was validated when a channel's *name* was displayed and
not when it keyed a *transmitter*, so an out-of-range slot cast to a byte and
went out on the public channel. It is now refused, by name, before anything
is sent, and `MeshCoreNeverTransmitsOnAChannelSlotTheNodeDoesNotHave` proves
nothing reaches the wire for slots 7, 256 and -1.

**2. History in one settings value.** Rewritten. History is now one value per
conversation plus a small index, and a save writes only what changed: an
unchanged history writes nothing at all, one new message rewrites one
conversation, and a forgotten conversation has its value deleted. A restart
writes nothing, because the store remembers what is already on disk.
Conversation values are keyed by a hash of the conversation id, so a station's
name cannot shape a settings key. The 0.4.1 single value is read on first run,
written back in the new layout, and removed.

History is also bounded in bytes, not only in messages: 192 KB per
conversation and 4 MB in total, each a named constant carrying its reason.
Over either, the oldest material is dropped on purpose. The failure mode being
designed against is the one you identified — a write that crosses the host's
document limit and takes the already-saved history with it.

**3. Unbounded peer tables, and a per-packet copy.** Both confirmed. The
Meshtastic node map is no longer rebuilt on every packet; both tables are
mutated in place under the lock they were already read under, and both are
capped (300 heard-but-unsaved MeshCore stations, 600 Meshtastic stations) with
oldest-first eviction that keeps our own node and anything favourited.
Eviction orders by Zeus's own receive order rather than the advert's
timestamp, since that timestamp is the sender's to set. Forgetting is logged
and the limit is shown in the panel.

**4. The mutation run.** This was the most useful finding in the set and it is
worth being explicit about the cause: the test harness set
`HostValidator.AllowLoopback = true` for the entire suite, because the
simulated nodes listen on 127.0.0.x. So the refusal path that the previous
pull request described in writing was never once exercised.

Loopback is now granted per test, through `[Test(Loopback = true)]`, and only
the eight tests that need a simulated node ask for it. Everything else runs
with the shipped default. Six new tests in `SecurityTests.cs` cover the claims
this PR makes: loopback refused by the validator, by name as well as by
literal, and by the real add-node endpoint; Internet addresses refused, and so
are addresses carrying a scheme, path, credentials or query; a node never
followed to a non-local address; only local candidates ever probed when a node
has moved; and a frame header claiming 60000 bytes skipped rather than
allocated.

`scripts/mutation-check.py` in the repository repeats your four mutations as an
acceptance check. Each one now turns the suite red:

```text
baseline: 48/48 passed
CAUGHT: allow loopback — 47/48
CAUGHT: drop the address check when following a node — 47/48
CAUGHT: drop the local filter on re-find candidates — 47/48
CAUGHT: remove the frame-size cap — 47/48
```

**5 and 6. Documentation describing things that do not ship.** Fair, and ours
to own. `options.md` is rewritten to cover only what the panel does, and it
now ends with a list of what is deliberately absent: rooms (posts arrive and
can be answered, but Zeus cannot log into a room), route tools, repeater and
node health, Store & Forward, Meshtastic channel management, and any contact
list of Zeus's own. The route-tools help page is deleted. `FEATURES.md` keeps
its build order but says which steps shipped.

The "Transmits automatically" column is gone from the options reference too.
Once the unshipped features came out, nothing in the panel transmits on its
own, which is a better thing to be able to state than a column of defaults.

**And a correction to the #19 text.** That pull request listed "channel keys"
among the persisted settings. That was wrong, and I corrected it in a comment
at the time: channel keys live on the node and nowhere else — the MeshCore
connector's `HasStoredSecrets` is `() => false`, and there is no room or
repeater password anywhere in the feature. The capability list below is
accurate.

## Also in this version

- **The Debug tab is now Logs**, with Everything / Messages / Problems
  filters, and every message in and out is logged — the channel or station,
  the byte count, SNR and hop count, never the text. Nothing logged an arrival
  before, which made "the node never decrypted it" and "Zeus filed it
  somewhere unexpected" indistinguishable from the log.
- **The version is shown in the panel**, and it comes from the manifest Zeus
  loaded rather than a constant in the code, so a bug report cannot quote a
  version nothing shipped with.

## Capabilities and permissions, and why

Declared: `NetworkAccess`, `PersistSettings`. Permissions: `network: true`,
`fileSystemRead: false`, `fileSystemWrite: false`.

**Network.** The feature's entire purpose is talking to LoRa nodes over the
local network: MeshCore's companion protocol on TCP 5000, Meshtastic's on TCP
4403. It connects only to nodes the operator configured, or to ones found by
an explicit Scan. **Public IP addresses are refused, and so is loopback** —
`HostValidator` accepts only private, link-local and CGNAT ranges, and
loopback only under a test flag whose shipped value is a constant the suite
asserts is false. Discovery is mDNS plus a probe of 4403/5000 across private
ranges, identity-confirmed, and it runs only on Scan, at setup, or when
re-finding a node that moved. There are no internet calls, no update checks
and no telemetry of any kind.

**Persisted settings.** Node addresses and identities, message history and
per-conversation mute flags. These go in the plugin settings store, which is
why there is no filesystem permission: the feature calls no `File` or
`Directory` API at all. **No keys or passwords are persisted** — channel keys
live on the node, and the feature holds no room or repeater password.

**Not used:** no `[FromServices]`, no `HttpContext.RequestServices`, no
environment variables, no reflection, no `HostDataDirectory`, no dynamic code
loading. PureSignal is never touched, and the feature never keys a
transmitter.

## Local gate results

| Gate | Result |
|---|---|
| `tools/validate-package.ps1` with every expected-metadata argument | exit 0 |
| `PackageSecurityScan scan` | **CLEAR** — 0 fail, 0 review, 2 info (the declared network use, and the ABI-1 capability note) |
| `tools/verify-source-build.ps1 -Phase All` from a clean clone of the pinned commit | **clear** — 0 fail, 0 review |
| `tools/validate-registry.ps1` with this entry | 24 plugins, 43 versions, exit 0 |
| `tools/test-community-custody.ps1` | passed |
| `tools/test-catalog-publication.ps1` | passed |
| Feature test suite | 48/48 |
| `scripts/mutation-check.py` | all four mutations caught |

## The 404 check, again

`downloadUrl` points at Zeus-SDR and will 404 until a maintainer mirrors the
package into custody. CONTRIBUTING says contributors are not expected to make
the HTTPS/checksum check pass; the package the scanners read is
`source.package`, on this feature's own repository, and that URL is live.

## Screenshots

Attached: dark and light, normal and narrow widths, 200% scaling, keyboard
focus, and the loading, empty, error, disconnected and unavailable states,
across all four tabs.
