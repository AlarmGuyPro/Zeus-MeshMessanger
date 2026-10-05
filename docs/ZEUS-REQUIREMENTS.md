# Zeus requirements, applied to Mesh Messenger

Every rule in the Zeus catalog's `README.md`, `CONTRIBUTING.md`, `AGENTS.md`,
pull-request template, SDK contracts and `tools/package-security-rules.md`
(catalog @ `453f360`), checked against this feature. Items marked **Decide**
need the owner's call before release.

## 1. Transmit safety

Zeus's bottom line is that transmit stays operator-led: a feature must never
key a transmitter on its own and must not touch PureSignal. Mesh Messenger
never touches the Zeus radio at all (no radio-control capability, no
PureSignal). The same spirit applies to the LoRa nodes, which are
transmitters too.

| Rule for Mesh Messenger | Why |
|---|---|
| **Every transmission we cause is operator-initiated**: Send, traceroute, path discovery, trace, advert, join room, repeater status. No auto-replies, no beacons, no repeating timers. | Zeus PR checklist: "never auto-keys a transmitter"; reviewers ask how transmit is kept operator-led. |
| **One transmission at a time across all nodes.** A single transmit coordinator serialises everything we initiate on every connector and waits for the previous send's airtime (MeshCore's suggested timeout, Meshtastic's queue status) before the next. | The Zeus "never two transmitters keyed at once" principle; two 900 MHz nodes at one site transmitting together desense each other. |
| **Hold mesh sends while Zeus is transmitting (MOX).** Sends wait, visibly ("Waiting for Zeus TX to end"), and fail after a short timeout rather than go out late. | Same principle across radios; LoRa node sits near the HF station. **Decide**: needs the read-only `ReadRadioState` capability (MOX only, never frequency/mode changes). |
| The few automatic transmissions (room re-login on connect) are **opt-in per room**, disclosed in the UI, spaced out and never retried in a loop. | Keeps automation explicit. **Decide** default (operator-felt default). |
| Physical separation is documented: the nodes' own automatic traffic (repeating, acks, telemetry) can't be coordinated by software. | Honest limit of the above. |
| Keys typed in the panel never reach Zeus hotkeys (Space = transmit). No global key listeners. | Scan rule `js-global-keys`; styling contract. Already in the panel. |

## 2. In-process stability

Community features load into the Zeus process with no sandbox; a crash or
leak in Mesh Messenger is a crash or leak in Zeus.

- `InitializeAsync` must finish within 10 s, `ShutdownAsync` within 5 s.
  Connectors start in the background and stop on cancellation; sockets are
  disposed; no thread outlives shutdown (the assembly is unloaded).
- No unobserved exceptions: every background loop catches and logs, then
  backs off. A node sending garbage must never take Zeus down.
- Bounded everything: frame sizes (Meshtastic 512, MeshCore 176) checked
  before allocating; message store capped; reconnect backoff capped.
- Parsers are fuzz-tested with random and truncated frames.

## 3. Network and data

| Requirement | How |
|---|---|
| Declare only what's used | `NetworkAccess` + `permissions.network: true` (TCP to nodes). `PersistSettings`. Nothing else. |
| No filesystem access | History, config, channel keys and room passwords live in the plugin settings store; no `File`/`Directory` APIs (scan rule `undeclared-filesystem`). |
| Network use is LAN-only and explainable | Connect only to operator-configured node/gateway addresses, and **refuse public IP addresses** in configuration. No internet calls, no update checks, no telemetry. |
| No secrets in logs or URLs | Channel keys and room passwords never logged, never in query strings, returned only by an explicit "show key" action. |
| No host internals | No `[FromServices]`, `HttpContext.RequestServices`, environment variables, reflection or `HostDataDirectory` (scan rules `host-services`, `environment`, `reflection`, `host-data`). |
| Scanner findings we will explain in the PR | Bundled `MeshMessenger.Mesh.dll` (`unexpected-assembly-ref`, review), the `meshtastic.org` share-URL prefix (`public-endpoint`, review). |

## 4. Build, package and review

- Rebuild contract, exact SDK pin, LF line endings, packaging from
  `zeus-build.json`: done.
- **No third-party NuGet packages anywhere in the repo.** The rebuild check
  lints every project and fails non-Microsoft packages that ship build-time
  code (xUnit, NUnit, MSTest adapters, SQLite bundles all do). So:
  - tests are a plain console project with a small assertion helper, run with
    `dotnet run --project tests/MeshMessenger.Tests`;
  - the gateway stores messages without SQLite (see GATEWAY.md).
- Tests are required "for behaviour whose regression could affect an operator
  or radio": routing (no cross-network fallback), the transmit coordinator,
  text-limit enforcement, parsers.
- UI: token-only scoped CSS, every state, screenshots in dark/light,
  normal/narrow, 200% scaling, keyboard focus, and the **transmit** state
  (use `--tx` for "sending / waiting for TX"); done in the scaffold except
  screenshots.
- Third-party notices: identify precisely what was derived from where (see 5).

## 5. Licensing — Decide

- The Meshtastic protocol definitions (`meshtastic/protobufs`) are
  **GPL-3.0**. Our codec is hand-written, but it encodes their message
  definitions field for field. MeshCore firmware and meshcore-cli are MIT.
- The feature is currently GPL-2.0-or-later. Relicensing to
  **GPL-3.0-or-later** before the first release (simple now, while there is
  one author) avoids any question about combining with GPL-3.0 material, and
  is compatible with the Zeus SDK's GPL-2.0-or-later. This is a judgement
  call, not legal advice.
- Either way, add `THIRD_PARTY_NOTICES.md` naming the upstream repositories,
  commits and licences the protocol work was derived from.

## 6. Catalog pull request etiquette

- The catalog repository's rules forbid mentioning AI assistants in anything
  visible in **that** repository (the listing PR, its description and
  commits), with credit to the human author. Write the listing PR yourself
  or strip such mentions. **Decide** whether to apply the same to this
  repository's commit trailers.
- One feature version per PR, `registry.json` only, `source` block with the
  exact commit, intake ZIP on a GitHub Release, never replaced.

## 7. Gateway (outside the catalog)

The Pi gateway is not part of the Zeus package and isn't reviewed by the
catalog, but the plugin's `Gateway` connector is. It must follow everything
above, the gateway enforces the same routing and transmit rules, and the
plugin's README must say clearly that the gateway is a separate, optional
install.
