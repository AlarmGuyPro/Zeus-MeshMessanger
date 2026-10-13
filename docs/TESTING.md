# Bench testing v0.1.0 on Windows

What has been tested so far: everything up to the radio, against simulated
nodes that speak the real companion protocols (23 automated tests, plus the
panel driven against them). What still needs real hardware: the firmware's
actual replies, Wi-Fi reconnects and range.

## 1. Provision the node(s)

**MeshCore (Heltec V4).** Follow
[Set up a Heltec V4 for MeshCore](help/setup-meshcore-heltec-v4.md). The Wi-Fi
build isn't on the web flasher (the Wi-Fi details are compiled in), so it is
built once with PlatformIO. It listens on TCP port 5000. Join your channels and
add contacts with the MeshCore app first, then disconnect the app: the node
serves one client at a time.

**Meshtastic (Heltec V4).** Flash current firmware, then in the app:
*Settings → Network → Wi-Fi on*, enter your network, and set the region and
preset. It listens on TCP port 4403 and announces itself on the network
(mDNS), so Scan usually finds it straight away.

Tip: let the router hand out addresses normally. Zeus remembers each node by
its identity, not its IP address, and finds it again if the address changes.

## 2. Build or download the package

Either use the ZIP attached to the conversation / release, or build it:

```powershell
git clone https://github.com/AlarmGuyPro/Zeus-MeshMessanger
cd Zeus-MeshMessanger
pwsh scripts/build-package.ps1
# -> artifacts\io.github.alarmguypro.meshmessenger\io.github.alarmguypro.meshmessenger-0.1.0.zip
```

Needs the .NET SDK pinned in `global.json` (10.0.112) and Node.js.

## 3. Install in Zeus

*Features → Community → Install local feature*, choose the ZIP, accept the
Network and Settings permissions, then open **Mesh Messenger** from the
workspace.

## 4. Test checklist

| # | Check | Expect |
|---|-------|--------|
| 1 | Settings → Add node → **Scan** | Each node listed with name, firmware and identity |
| 2 | **Add** each one | Node bar shows `● connected` within a few seconds |
| 3 | Send on a channel from another radio | Appears in the inbox with SNR and hops |
| 4 | Reply from Zeus | Shows `Sent`; other radios receive it on the **same** network only |
| 5 | DM from another radio, reply | Goes to `Delivered` with round-trip time (MeshCore) or `Delivered` on recipient ack (Meshtastic) |
| 6 | Close Zeus, send messages to the node, reopen | MeshCore: they appear under "Received while Zeus was closed" |
| 7 | Reboot the node / router so its IP changes | Zeus shows "looking for it", then reconnects |
| 8 | Put a different node at the old IP | Zeus refuses it and says so |
| 9 | Nodes tab | Contacts / node list with last heard, SNR, hops |
| 10 | Room server (MeshCore) | Posts arrive in a Room conversation with author names |
| 11 | Nodes → **Send advert**, then check another station | It lists this node within a minute |
| 12 | Message a station shown as **heard · not saved** | Zeus saves it on the node, then sends |
| 13 | **Logs** tab | Connects, sends, arrivals and acknowledgements; the Everything / Messages / Problems filters and **Copy all** work |
| 14 | A failed message → **Send again**, then **Clear them** | Retries, then tidies the thread |

If something fails, the node's status line (hover it in the node bar) has the
reason. Note the firmware version from the Nodes tab when reporting.

## Looking at the panel without hardware

The preview serves the real panel against the real backend with simulated nodes
behind it, and the screenshot script takes the whole set the catalog requires.
Both live in the repository so they do not have to be rebuilt each release.

```powershell
cd web; npm run build; cd ..
dotnet run --project tests/MeshMessenger.Tests -- --preview 5319
# in another shell, once:
cd tests/preview; npm install
node screenshots.mjs --port 5319 --out ../../screenshots
```

The awkward states are forced rather than waited for: the preview host has
`/preview/slow/{on}` and `/preview/broken/{on}`, which the script uses for the
loading and error screenshots, and it deletes every node at the end for the
empty ones. Its dependencies sit outside `web/` on purpose, so they are not part
of the build the catalog rebuilds and scans.

### Host quirks

```powershell
node tests/preview/regression.mjs --port 5319
```

Renders the panel under differences between a plain browser and an embedded webview, and
fails if the error boundary ever appears. Run it before a release: the browser the panel is
developed in is not the browser it ships in, and the one fault this caught — an effect
returning the value of `scrollTo`, which is undefined in Chromium and the element in Zeus —
was invisible everywhere except on a live bench.

Screenshots are never committed: the catalog's rebuild check fails on a tracked
binary. Deliver them as a ZIP.
