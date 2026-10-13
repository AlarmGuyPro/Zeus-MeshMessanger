# Set up a Heltec V4 as a Meshtastic node for Zeus

This takes about 20 minutes if your Wi-Fi cooperates. You need a Windows PC, a
USB-C **data** cable (some cables only charge), and the Heltec V4 with its LoRa
antenna attached.

> **Always attach the LoRa antenna before powering the board.** Transmitting
> without one can damage the radio.

Meshtastic is easier to set up than MeshCore, because its Wi-Fi details are a
normal setting rather than something compiled into the firmware. There is no
PlatformIO step: flash from the browser, then configure.

The plan:

1. Flash with the web flasher.
2. Set the region, name and role.
3. Turn on Wi-Fi and find the node's address.
4. Add it in Zeus.

---

## Step 1: Flash the firmware

1. Attach the antenna, then plug the board into the PC.
2. Open **`flasher.meshtastic.org`** in Chrome or Edge. (Firefox and Safari
   can't talk to serial ports.)
3. Choose **Heltec V4** and the current stable release.
4. Choose **Full erase and install**, not Update.

That last choice matters. A partial flash leaves the old partition layout in
place, and the symptoms are confusing: the radio works, Wi-Fi may even join,
but services that depend on the filesystem never start.

If the PC can't see the board, hold **PRG**, tap **RST**, release **PRG**, and
try again.

## Step 2: Region, name and role

Set the region first — **a Meshtastic node will not transmit at all until its
region is set**, which looks exactly like a broken radio.

The commands below use the Python CLI, which you'll want anyway for
troubleshooting. Install it once:

```powershell
pip install --upgrade "meshtastic[cli]"
```

If Windows answers with a Microsoft Store advert instead of running Python, use
the `py` launcher: `py -m pip install ...` and `py -m meshtastic ...` for
everything that follows. Find your COM port with:

```powershell
[System.IO.Ports.SerialPort]::getportnames()
```

Then set the basics in one command — the node reboots after each one, so
chaining them saves time:

```powershell
py -m meshtastic --port COM12 --set lora.region US --set device.role CLIENT --set-owner "KQ4WLR Zeus" --set-owner-short "zeus"
```

### Which role?

**CLIENT.** It takes part normally and relays for its neighbours, and it is the
right answer for a node sitting at your operating position.

| Role | Use it when |
|------|-------------|
| **CLIENT** | The default. Your station node. |
| CLIENT_MUTE | A second node in the same room, so it doesn't repeat everything twice. |
| ROUTER / REPEATER | A stationary node in a genuinely good location — a hilltop or a rooftop. |

Don't set ROUTER on a node that sits on your bench. Routers are preferred for
relaying, so a badly placed one burns hops that should have gone to a
better-placed node, and several routers near each other collide.

### Quieten the GPS probe

The Heltec V4 has no GNSS module, but the firmware probes for one at every
baud rate, forever. It fills the log and makes real problems hard to see:

```powershell
py -m meshtastic --port COM12 --set position.gps_mode DISABLED
```

## Step 3: Wi-Fi

```powershell
py -m meshtastic --port COM12 --set network.wifi_enabled true --set network.wifi_ssid "YourSSID" --set network.wifi_psk "yourpassword"
```

Three things to know before you worry:

- **Turning on Wi-Fi turns Bluetooth off.** ESP32 does one or the other. Your
  phone's Bluetooth connection dropping is expected, not a failure — reconnect
  through **Network** in the app instead.
- **2.4 GHz only**, joined as a client. There is no access-point mode.
- **The first reboot takes an extra 20–30 seconds** while it generates SSL keys.

### Find the address

The OLED cycles to a page showing the IP once it's up. Otherwise look at your
router's DHCP leases for a hostname like `Meshtastic-xxxx`. With a single
Meshtastic device on the network, `meshtastic.local` resolves too.

**Give it a DHCP reservation.** Zeus remembers the address you add, and a node
that moves is a node you have to fix later.

Confirm from the Zeus PC:

```powershell
Test-NetConnection 192.168.1.50 -Port 4403
```

`TcpTestSucceeded : True` and you're done here.

## Step 4: Add it in Zeus

**Settings → Add node → Scan** finds Meshtastic nodes that advertise over mDNS,
as long as the node and the Zeus PC are on the same network segment. If your
nodes live on another VLAN, mDNS won't reach you — type the address instead,
with network **Meshtastic** and port **4403**.

Then open **Logs**. You should see the connect, the configuration handshake,
and the node reporting its firmware and radio settings.

A new node shows an empty Nodes tab and a single channel with no messages.
That's correct — it hasn't heard anyone yet.

---

## When Wi-Fi won't connect

This is where the time goes, so work from evidence rather than guesses. Read
the device's own log:

```powershell
py -m meshtastic --port COM12 --noproto
```

Start it, press **RST**, and watch the boot.

### The reason code tells you which problem you have

| Reason | Meaning | What it rules out |
|---|---|---|
| **201 NO_AP_FOUND** | The node can't see that SSID. | Check the spelling, that it's 2.4 GHz, and that it isn't hidden. |
| **202 AUTH_FAIL** | The AP **was found** and rejected it. | Band, range and SSID spelling are all fine. It's credentials, AP settings, or signal quality. |
| **204 HANDSHAKE_TIMEOUT** | Auth started and stalled. | Usually a marginal link. |

A useful tell: once the log's timestamps change from `??:??:??` to a real clock,
the node has reached an NTP server, which means Wi-Fi *and* internet are
working.

### Check the password character by character, first

202 means the AP rejected the credentials, so start with the credentials — and
read back what is actually stored rather than what you believe you typed:

```powershell
py -m meshtastic --port COM12 --get network.wifi_psk
```

Compare it against the AP's password one character at a time. A single case
difference is enough, and it is invisible when you are reading quickly:
`Bark1ngD0gs` and `Bark1ngd0gs` look the same at a glance and are not the same
password. A trailing space from a copy and paste does it too.

This is the boring answer and it is usually the right one.

### Then: which access point is it talking to?

In a house with several APs, a marginal link to a distant one produces repeated
AUTH_FAIL that looks exactly like a wrong password, and band steering hands the
node back to the wrong radio each time.

**Lock the node to the access point in the same room.** In UniFi that's
`Client Devices → [the node] → Lock to AP`. Other controllers have the same
idea under a different name. This is worth doing for every mesh node, since
they never move.

Change one of these at a time. When the node that prompted this guide finally
joined, both had been changed — the stored password had a lowercase letter
where we had been testing an uppercase one, *and* it had been locked to the
near AP — so which one actually fixed it is honestly unknown. Changing them one
at a time would have told us.

### Don't trust ping

A node can look alive when it isn't. If a router or AP answers ICMP on behalf
of a client it still has in its table — proxy ARP — you get ping replies and a
matching MAC address for a device that is not actually on the network. The
giveaway is that **no TCP port is open**, and that the ping's TTL is 64 (a
Linux-based router) rather than the 255 an ESP32's own stack replies with.

So: ping proves nothing on its own. `Test-NetConnection <ip> -Port 4403` and
the serial log are the real evidence.

### Access-point settings that break ESP32 nodes

If you're building an SSID for mesh nodes and other IoT gear, these settings
avoid every problem in this section:

| Setting | Use |
|---|---|
| Security | **WPA2-PSK** only — not WPA2/WPA3 transition, not WPA3 |
| PMF (Protected Management Frames) | **Optional** or disabled, never Required |
| Band | **2.4 GHz only** |
| Fast Roaming (802.11r) | **Off** |
| Private Pre-Shared Keys | **Off** — a per-device key list gives AUTH_FAIL to anything not on it |
| Minimum RSSI | **Off** — it kicks exactly the devices you want to stay put |
| Client isolation | **Off**, or Zeus can't reach the node |

### If the nodes live on another VLAN

Zeus always dials the node; the node never initiates anything toward your LAN.
So one narrow rule in the safe direction is enough:

```
allow  <Zeus PC>  ->  <node>  tcp/4403      (MeshCore nodes: tcp/5000)
```

**Don't add an mDNS reflector** to make Scan work across the VLAN — it leaks
discovery traffic both ways and is a far bigger hole than the single rule
above. Type the node's address in Zeus instead and give it a reservation.

### If nothing here fits

Collect these three things before asking anyone for help: the output of
`py -m meshtastic --port COM12 --info`, the Wi-Fi portion of the `--noproto`
log through a reboot, and **Copy all** from Zeus's [Logs tab](logs.md).
