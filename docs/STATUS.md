# Where this project stands

Last updated 12 October 2026, after submitting 0.4.1 to the Zeus community
catalog. Read this first when picking the project back up.

## Shipped

**0.4.1** is built, installed, running against real hardware, and **submitted**:
[PR #19](https://github.com/Zeus-SDR/zeus-community-features/pull/19) on
`Zeus-SDR/zeus-community-features`.

| | |
|---|---|
| Pinned commit | `f4e26ca7cba194ae713a25d7b60d8fa653bcb391` |
| Release | `v0.4.1` on this repo, tagged at that commit |
| SHA-256 | `a854b549ce4717da09a16558cf533584771ae9c60649347de2c9b63ab28c53fc` |
| Security scan | CLEAR, 0 fail, 0 review |
| Source rebuild | passed |
| Tests | 32/32 |

Two checks on the PR were red and neither was ours: the custody download 404s
until a maintainer mirrors the package, which the contributing guide says to
expect, and one Windows leg of the catalog's own scanner self-test went flaky.
Both are explained in a comment on the PR.

**0.4.1 shipped in the Zeus release of 12 October**, so it is in strangers'
hands now.

## The review, and what it means for 1.0

A maintainer reviewed PR #19 and returned six findings. All six were confirmed
against the code; they are tasks **#47 to #51** and they all block 1.0 (#46).
A reply accepting them has been posted.

| | Finding | Shape |
|---|---|---|
| #47 | A MeshCore channel index that is not a real slot casts to a byte and transmits on the public channel | On-air correctness. Not reachable from the panel, only by a direct API call |
| #48 | Four security mutations, including allowing loopback, left all 32 tests passing | The important one. See below |
| #49 | The whole history is one settings value rewritten every 2 s; past ~16 MB the host deletes the old value first, so it is lost | Largest piece of work |
| #50 | `_heard` and `_nodes` grow without bound; the Meshtastic map is copied per packet | Memory on busy or MQTT-bridged meshes |
| #51 | `options.md` documents rooms, traceroute, path tools and repeater status, none of which ship; and the PR text wrongly says channel keys are persisted | Ours to own |

**#48 is the one to take first.** The others are defects. That one says the
test suite does not protect the properties the submission claims in writing:
the harness sets `AllowLoopback = true` globally because the fake nodes listen
on loopback, so the refusal path is never exercised. Their four mutations
become our acceptance test — each must turn the suite red.

Two of the findings are worth remembering as patterns rather than bugs. In
#47, the correct slot check already existed twenty lines away in `TitleFor`:
the code validated an index when displaying a name and not when keying a
transmitter. In #51, `options.md` had been describing features that did not
exist since before the playbook warned against exactly that.

Nothing in the set warranted a hotfix for people who installed 0.4.1 today.

Every test build lives on the `builds` branch, newest first, with a SHA-256
beside each. Archives are kept off `main` because the catalog's rebuild check
rejects a tracked binary.

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

The Zeus PC is `192.168.50.60`, wired, same subnet as both nodes.

Two things that cost hours and are worth remembering:

- **Direct messages need both nodes to hold each other.** MeshCore decrypts a
  DM using the sender's key from its own contacts, so a station that hasn't
  saved you cannot read what you send it. Neither end reports anything useful
  when this is wrong.
- **A Wi-Fi reason code of 202 means the password was rejected**, not that the
  radio or the band is wrong. Check the stored PSK character by character
  before touching anything else; `Bark1ngD0gs` and `Bark1ngd0gs` look identical
  at a glance. Locking the node to the access point in the same room matters
  too, but check the password first.

**No second Meshtastic node exists**, so Meshtastic direct messages have never
been tested against real hardware. Everything Meshtastic has only ever run
against the simulated node in the test suite, except the connect, the node
list and mDNS discovery.

## What to do next

Run `TaskList` for the live list. **The five review items (#47–#51) come
first** — they are conditions of the listing, not options. #48 before the rest.

Then, still inside 1.0:

1. **A second Meshtastic node.** Direct messages on that network have never
   run over the air; the live node has heard no other Meshtastic station. A
   two-network feature calling itself 1.0 should have sent a real one.
2. **#39 Show the plugin version in the panel.** Small, and every bug report
   from here on wants it. The version currently lives in four files that must
   be bumped together, which is three too many.
2. **#45 Make the package byte-reproducible.** Rebuilding the same commit
   produces a working but byte-different assembly, so a release ZIP can never
   be regenerated. That is a trap for whoever cuts 0.5.0.
3. **#41 A durable address book.** The decision is already made and recorded:
   the node stays the source of truth, Zeus keeps a record that can push back
   into it. Solves contacts lost to recycling and to a reflash.
4. **#40 Repeater health.** Protocol is understood and written down. The
   constraint that matters is airtime: 2-hour polling, off by default.
5. **#38 Meshtastic channel management**, now that there is hardware to try it
   against.
6. **#35 The arrival sound**, **#25**, **#27**, **#28** when they come up.

The two days after this was written were meant for using 0.4.1 on the bench.
Anything found there outranks the list above.

## Where things live

| | |
|---|---|
| Panel | `web/src/` — `Panel`, `Inbox`, `Nodes`, `Settings`, `Debug`, `Picker` |
| Backend | `src/MeshMessenger/` — `Api/Endpoints.cs`, `Services/`, `MeshMessengerPlugin.cs` |
| Mesh code | `src/MeshMessenger/Mesh/` — `Core/`, `Connectors/`, `Discovery/` |
| Tests | `tests/MeshMessenger.Tests/` — `dotnet run --project tests/MeshMessenger.Tests` |
| Operator help | `docs/help/` |
| Submission | `docs/submission/` |
| Process notes | `docs/ZEUS-PLUGIN-PLAYBOOK.md` |

The mesh code lives **inside** the plugin project rather than in a library of
its own. That is deliberate: the catalog's rebuild check allows neither `..` in
an MSBuild item path nor `LinkBase`, and a second DLL draws a review finding.
Splitting it back out is a folder move when the gateway is real.

Tests use fake nodes that speak each protocol over a loopback socket, so the
suite runs with no hardware. The preview harness that produced the review
screenshots is rebuilt per session in the scratchpad; it serves the real
backend with those fakes behind it.
