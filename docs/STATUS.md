# Where this project stands

Last updated 13 October 2026, with 1.0.0 built and every local gate green.
Read this first when picking the project back up.

## Shipped, and listed

**0.4.1 is in the Zeus community catalog.**
[PR #19](https://github.com/Zeus-SDR/zeus-community-features/pull/19) was
merged, `registry.json` on `main` carries the entry, and the package is
mirrored into custody. It shipped in the Zeus release of 12 October.

**1.0.0 is built and ready to submit**, but **not yet released and not yet on
the bench**. It answers all six review findings.

| | |
|---|---|
| Pinned commit | `986bbd9bb6273e4fbcb921110b5cb5adfab4d95e` |
| SHA-256 | `bbb345149e35fed650467243581e8e0f908f26d2d3321289cecdbe39742b9986` |
| Package validation | pass |
| Security scan | CLEAR, 0 fail, 0 review |
| Source rebuild | clear, from a clean clone of the pinned commit |
| Registry edit | `validate-registry`, `test-community-custody` and `test-catalog-publication` all pass with the 1.0.0 entry appended |
| Tests | 48/48 |
| Mutation check | all four of the maintainer's mutations turn the suite red |

**Nothing is published yet.** The release and the catalog PR wait until 1.0.0
has run on the bench, because a published release can never be replaced — the
registry pins the SHA-256 of one exact archive.

## What 1.0.0 changed

The six review findings, all confirmed against the code before being fixed:

| | Finding | What was done |
|---|---|---|
| #47 | A MeshCore channel index that is not a real slot cast to a byte and transmitted on the public channel | Refused by name before anything is sent, with a test that proves nothing reaches the wire |
| #48 | Four security mutations left all 32 tests passing | Loopback is granted per test now, not suite-wide; six new tests in `SecurityTests.cs`; `scripts/mutation-check.py` repeats the maintainer's run as an acceptance check |
| #49 | The whole history was one settings value rewritten every 2 s | One value per conversation plus an index; only what changed is written; 192 KB per conversation and 4 MB in total, enforced by dropping the oldest on purpose |
| #50 | `_heard` and `_nodes` grew without bound, and the Meshtastic map was copied per packet | Both mutated in place and capped, oldest-first, by Zeus's own receive order rather than the sender's timestamp |
| #51 | Docs described rooms, route tools, repeater status and Store & Forward, none of which ship | `options.md` rewritten and ends with what is absent; the route-tools page deleted; the PR's channel-keys claim corrected |

And beyond the review:

- **The Debug tab is Logs**, with Everything / Messages / Problems filters, and
  **every message in and out is logged** — channel or station, byte count, SNR,
  hops, never the text. Nothing logged an arrival before, which is why a silent
  log could not distinguish "the node never decrypted it" from "Zeus filed it
  somewhere unexpected".
- **The version shows in the panel**, read from the manifest Zeus loaded rather
  than a constant in the code. `plugin.json` is the only place it is written;
  the build refuses to run if the two build files that copy it disagree.
- **The preview harness and screenshot script now live in the repository**
  (`--preview` on the test project, `tests/preview/screenshots.mjs`). Twenty-four
  screenshots in about a minute, instead of rebuilding the harness every release.

## What to do next

1. **Run 1.0.0 on the bench.** Install the delivered ZIP. The first thing worth
   doing is the hashtag channel again, because the Logs tab can now answer it:
   send on `#Test` from a station that genuinely holds that channel and look for
   `RX Channel 1 ("#Test"): received N bytes`. Present means the node decrypted
   it and Zeus filed it; absent means it never arrived in a form the node could
   read, which on a channel is nearly always the two ends holding different keys.
   Names are case-sensitive: `#test` and `#Test` are different channels.
2. **Then publish**, with `docs/submission/`: `submit-1-release.ps1` creates the
   release, `submit-2-registry.ps1` appends the version to the existing catalog
   entry. Take the screenshots first and attach them.
3. **A second Meshtastic node.** Meshtastic direct messages have still never run
   over the air; the live node has heard no other Meshtastic station. That is the
   largest untested area in the feature.
4. **#45 Make the package byte-reproducible.** Rebuilding the same commit
   produces a working but byte-different assembly, so a release ZIP can never be
   regenerated. A trap for whoever cuts 1.0.1.
5. **#41 A durable address book**, **#40 repeater health** (protocol understood,
   2-hour polling, off by default), **#38 Meshtastic channels**, **#35 arrival
   sound**, **#25**, **#27**.

## The bench

| | MeshCore | Meshtastic |
|---|---|---|
| Board | Heltec V4 | Heltec V4 |
| Name | KQ4WLR Zeus | KQ4WLR Meshtastic (`wlr`) |
| Address | 192.168.50.100:5000 | 192.168.50.140:4403 |
| Firmware | companion Wi-Fi v1.17.1 | 2.7.26.54e0d8d |
| Radio | 910.525 MHz, 62.5 kHz, SF7, CR5, 10 dBm | LongFast, US, SF11, 250 kHz |
| Contacts | ~346 of 350, recycling on | — |
| Node DB | — | 1 (itself; no other Meshtastic station in range) |

The Zeus PC is `192.168.50.60`, wired, same subnet as both nodes. A Wio is the
other end for MeshCore tests.

Three things that cost hours and are worth remembering:

- **Direct messages need both nodes to hold each other.** MeshCore decrypts a
  DM using the sender's key from its own contacts, so a station that hasn't
  saved you cannot read what you send it. Neither end reports anything useful.
- **A Wi-Fi reason code of 202 means the password was rejected**, not that the
  radio or the band is wrong. Check the stored PSK character by character before
  touching anything else; `Bark1ngD0gs` and `Bark1ngd0gs` look identical.
- **A channel's key comes from its name exactly as typed.** `#test` and `#Test`
  derive different keys and cannot hear each other. The Nodes tab proves the key
  on the node is right: it only labels a channel **Hashtag** when the secret it
  read back matches SHA-256 of the name.

## Where things live

| | |
|---|---|
| Panel | `web/src/` — `Panel`, `Inbox`, `Nodes`, `Settings`, `Logs`, `Picker` |
| Backend | `src/MeshMessenger/` — `Api/Endpoints.cs`, `Services/`, `MeshMessengerPlugin.cs` |
| Mesh code | `src/MeshMessenger/Mesh/` — `Core/`, `Connectors/`, `Discovery/` |
| Tests | `tests/MeshMessenger.Tests/` — `dotnet run --project tests/MeshMessenger.Tests` |
| Preview and screenshots | `--preview` on the test project, `tests/preview/` |
| Mutation check | `scripts/mutation-check.py` |
| Operator help | `docs/help/` |
| Submission | `docs/submission/` |
| Process notes | `docs/ZEUS-PLUGIN-PLAYBOOK.md` |

The mesh code lives **inside** the plugin project rather than in a library of
its own. That is deliberate: the catalog's rebuild check allows neither `..` in
an MSBuild item path nor `LinkBase`, and a second DLL draws a review finding.

Every test build lives on the `builds` branch, newest first, with a SHA-256
beside each. Archives and screenshots are kept off `main` because the catalog's
rebuild check rejects a tracked binary.
