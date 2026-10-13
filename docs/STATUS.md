# Where this project stands

Last updated 13 October 2026, with 1.0.1 built, gated and bench-tested.
Read this first when picking the project back up.

## Shipped, and listed

**0.4.1 is in the Zeus community catalog.**
[PR #19](https://github.com/Zeus-SDR/zeus-community-features/pull/19) was
merged and the package is mirrored into custody. It shipped in the Zeus
release of 12 October.

**1.0.1 is built, gated and bench-tested**, and is the one to submit. (1.0.0
was built and gated but never released: a day of real use produced seven
findings, and two builds must not both answer to the same version now that the
panel shows it.)

| | |
|---|---|
| Pinned commit | `21a07e23ffed0e49469886e9b6204e3ef6e3ee7b` |
| SHA-256 | `abddb23cce3ef7478543c384b6dd96b0af58e387d566802478b961b226c5d36b` |
| Package validation | pass |
| Security scan | CLEAR, 0 fail, 0 review |
| Source rebuild | clear, from a clean clone of the pinned commit |
| Registry edit | `validate-registry` and `test-community-custody` pass with 1.0.1 appended; the exact diff is `docs/submission/registry-1.0.1.patch` |
| Tests | 55/55 |
| Mutation check | all four of the maintainer's mutations turn the suite red |

**Nothing is published yet.** `docs/submission/` is ready: `submit-1-release.ps1`
cuts the release, `submit-2-registry.ps1` appends the version to the existing
catalog entry and refuses to proceed if the published 0.4.1 object does not
survive its own edit. Take the screenshots first and attach them.

## What 1.0 answered from the review

All six findings, each confirmed against the code before being fixed:

| | Finding | What was done |
|---|---|---|
| #47 | A channel index that is not a real slot cast to a byte and transmitted on the public channel | Refused by name before anything is sent |
| #48 | Four security mutations left all 32 tests passing | Loopback granted per test, not suite-wide; six tests in `SecurityTests.cs`; `scripts/mutation-check.py` repeats the run as an acceptance check |
| #49 | The whole history was one settings value rewritten every 2 s | One value per conversation plus an index; only what changed is written; 192 KB and 4 MB budgets enforced by dropping the oldest on purpose |
| #50 | Peer tables grew without bound, and one was copied per packet | Both mutated in place and capped, oldest-first by Zeus's own receive order |
| #51 | Docs described features that do not ship | `options.md` rewritten, ending with what is absent; route-tools page deleted |

## What the bench added, after that

A day of real use on a busy mesh, in the order it was found:

1. **Hashtag channels are case-sensitive.** `#test` and `#Test` derive
   different keys and cannot hear each other. The join form now warns the
   moment a `#` name is typed with a capital in it.
2. **Newest message at the top**, like a mail client, with the "while Zeus was
   closed" block below this session's traffic.
3. **Clearing a long contact list buried the node.** Every removal re-read the
   whole list — 323 removals meant 323 full dumps — and the first unanswered
   keep-alive then ended the session, so the node appeared to vanish. Bulk
   removal now paces itself, reads back once, and survives a node that falls
   behind.
4. **A full-list warning that contradicted itself** four seconds after the node
   saved a new contact: it never checked whether recycling was on.
5. **Push code 0x88 is the firmware's packet log**, pushed constantly. Ignored
   now rather than drowning the Logs tab.
6. **Contacts came back after being removed**, because the node re-adds every
   station it hears. The Nodes tab now says so, counted from the node's own
   announcements rather than inferred.
7. **And then the setting itself.** `What this node saves by itself` on the
   Nodes tab: the mode, the per-kind choices, and the hop limit. Written
   against the firmware source, which showed that the per-kind bits are ignored
   entirely unless the node is also made selective — so the obvious control
   would have done nothing. See `docs/protocols/meshcore.md`.

## One report that turned out not to be a bug

A bench report of "viewing the channels crashed Zeus and reset the workspace",
minutes after installing 1.0.1. Zeus's own log says otherwise, and the sequence
is worth keeping because it will happen again:

```text
18:46:39  POST /shutdown   engine.shutdown accepted     <- requested, not a fault
18:46:43  antennagenius [info] Stopped.                 <- orderly plugin teardown
18:46:44  PowerStation stopped
18:47:00  station-engine start pid=26008                <- a fresh engine process
18:47:01  Loaded plugin io.github.alarmguypro.meshmessenger v1.0.1
18:47:01  Mesh Messenger initialized with 2 node(s)
```

Installing a feature restarts the Zeus engine, and the restart resets the
workspace layout. There is no exception anywhere in the window, the plugins
shut down one at a time with their own goodbyes, and the feature came back up
clean. The panel's own endpoints were not called once in the three minutes that
followed, so the panel had not even been opened yet.

**The supervisor log is not the one to ask.** `support-agent.log` carries
heartbeats and process starts and records 36 restarts in five days, so a restart
in it proves nothing. `zeus-app.log` and its rotations are where the plugin
lines and any exception live.

The error boundary added that evening stays. A feature still should not be able
to take its host's window down, and it costs nothing to make sure it cannot.

## What to do next

1. **Publish 1.0.1**, with `docs/submission/`. Take the screenshots
   (`tests/preview/screenshots.mjs`) and attach them to the pull request.
2. **A second Meshtastic node.** Meshtastic direct messages have still never
   run over the air. That is the largest untested area in the feature.
3. **#45 Make the package byte-reproducible**, so a release ZIP can be
   regenerated. A trap for whoever cuts the next one.
4. **#41 a durable address book**, **#40 repeater health**, **#38 Meshtastic
   channels**, **#35 arrival sound**, **#25**, **#27**.

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
