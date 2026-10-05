# Bench testing v0.1.0 on Windows

What has been tested so far: everything up to the radio, against simulated
nodes that speak the real companion protocols (23 automated tests, plus the
panel driven against them). What still needs real hardware: the firmware's
actual replies, Wi-Fi reconnects and range.

## 1. Provision the node(s)

**MeshCore (Heltec V4).** Flash the *Companion Radio — Wi-Fi* build with the
web flasher. That build has your Wi-Fi name and password compiled in (there is
no setup screen). If the flasher you use doesn't offer Wi-Fi fields for your
board, build it yourself with `WIFI_SSID` / `WIFI_PWD` set. It listens on TCP port 5000. Join your channels and
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

If something fails, the node's status line (hover it in the node bar) has the
reason. Note the firmware version from the Nodes tab when reporting.
