# Set up a Heltec V4 as a MeshCore node for Zeus

This takes about 30–45 minutes the first time, most of it waiting for
downloads. You need a Windows PC, a USB-C **data** cable (some cables only
charge), the Heltec V4 with its LoRa antenna attached, and your phone with
the MeshCore app.

> **Always attach the LoRa antenna before powering the board.** Transmitting
> without one can damage the radio.

## Why it isn't a one-click flash

MeshCore's Wi-Fi firmware has your Wi-Fi name and password **built into it**,
so MeshCore doesn't publish it, and the web flasher doesn't list it. You build
it once on your PC with PlatformIO. That sounds harder than it is: you edit
one small file and press one button.

(MeshCore's development branch adds commands to change Wi-Fi details without
rebuilding. Once that ships in a release, this guide gets shorter.)

The plan:

1. Flash the normal **Bluetooth** firmware with the web flasher and set the
   node up from your phone (name, radio settings, channels). This is the easy,
   familiar way to configure it.
2. Build the **Wi-Fi** firmware on your PC and load it over USB **without
   erasing**. The node keeps its identity, name, radio settings, channels and
   contacts, and now joins your Wi-Fi.
3. Add it in Zeus with **Scan**.

## Which board do you have?

| Board | PlatformIO environment |
|-------|------------------------|
| Heltec WiFi LoRa 32 **V4** (OLED screen, 2 MB PSRAM: the common one) | `heltec_v4_companion_radio_wifi` |
| Heltec V4 **R8** (8 MB PSRAM) | `heltec_v4_r8_companion_radio_wifi` |
| Either, fitted with Heltec's **TFT** display kit | the `_tft_` version of the above |

Not sure? The listing or box says 8 MB PSRAM if it's an R8. You can also check
during step 3: when PlatformIO uploads, the log prints a line like
`Features: WiFi, BLE, Embedded PSRAM 2MB`. If it says 8MB, use the R8
environment.

---

## Step 1: Bluetooth firmware and phone setup

1. Plug the board into the PC with the USB-C cable.
2. In **Chrome or Edge** (Firefox can't do this), open
   **<https://flasher.meshcore.io>**.
3. Choose your device (**Heltec V4**; or the 8 MB/R8 entry if that's yours),
   then the **Companion (Bluetooth / BLE)** firmware, then the latest version.
4. Tick **Erase device**. This is a first install, so there's nothing to keep.
5. Click **Flash**, pick the board's COM port in the browser pop-up
   (usually "USB JTAG/serial debug unit"), and wait for it to finish.
   - If no port appears, or flashing fails: hold the **PRG** (BOOT) button,
     tap **RST**, release **PRG**, then try again. This forces the board into
     download mode.
6. The screen shows a Bluetooth PIN. In the **MeshCore app**, add a device,
   pick the node and enter the PIN.
7. In the app, set:
   - **Name:** something people will recognise as your Zeus station, e.g.
     `KQ4WLR Shack`. Shorter names leave more room in channel messages.
   - **Radio settings:** exactly the same as your repeater and other devices
     (frequency, bandwidth, spreading factor, coding rate). If they don't
     match, the node hears nothing. Copy them from another of your devices.
   - **Channels:** join the same public and hashtag channels your other
     devices use, and add any private ones.
   - Send a test message on a channel from another device and check it
     arrives. Then the radio side is right.
8. Disconnect the app (Bluetooth goes away in step 3 anyway).

## Step 2: Install the build tools (one time)

1. Install **Git for Windows**: <https://git-scm.com/download/win>
   (defaults are fine).
2. Install **Visual Studio Code**: <https://code.visualstudio.com>.
3. In VS Code: **Extensions** (Ctrl+Shift+X) → search **PlatformIO IDE** →
   **Install**. Let it finish setting up (watch the bottom-right corner), then
   restart VS Code when asked.
4. Open a terminal (in VS Code: **Terminal → New Terminal**) and get the
   MeshCore source at the current release:

   ```powershell
   cd $HOME\Documents
   git clone https://github.com/meshcore-dev/MeshCore.git
   cd MeshCore
   git tag --list "companion-v*" --sort=-creatordate | Select-Object -First 3
   git checkout companion-v1.17.1
   ```

   Use the newest `companion-v…` tag from the list (1.17.1 was current when
   this was written). Use the same version as the Bluetooth firmware you
   flashed in step 1, or newer.

5. **File → Open Folder…** → `Documents\MeshCore`. PlatformIO notices the
   project and spends a few minutes downloading the ESP32 toolchain the first
   time. Wait until the bottom bar settles.

## Step 3: Build and load the Wi-Fi firmware

1. In the `MeshCore` folder, create a file named **`platformio.local.ini`**
   (MeshCore reads this file automatically, and Git ignores it, so your
   password never ends up anywhere else). Copy in the contents of
   [`platformio.local.ini.example`](../firmware/meshcore/platformio.local.ini.example)
   from this repo, then change the two lines:

   ```ini
     -D WIFI_SSID='"YourWiFiName"'
     -D WIFI_PWD='"YourWiFiPassword"'
   ```

   Keep both sets of quotes exactly as shown: `'"` before, `"'` after.
   - The network must be **2.4 GHz**. The board has no 5 GHz radio. If your
     router uses one name for both bands, that's fine.
   - If your password contains `'`, `"` or `\`, those need escaping. Easiest
     is a separate IoT network or guest network with a simpler password.
   - If you have an **R8** board, use the R8 section of the example instead.
2. In the PlatformIO sidebar (the alien-head icon), open **Project Tasks** →
   **`zeus_meshcore_v4`** (or `zeus_meshcore_v4_r8`) → **Upload**.
   - The first build takes a few minutes. Later ones are quick.
   - **Use Upload, not "Erase Flash" or "Upload Filesystem Image".** A plain
     upload replaces the firmware and leaves the node's saved settings alone.
   - If the upload can't find the board, use the PRG/RST trick from step 1.
3. When it finishes, the board restarts. Within a few seconds the home screen
   shows **`IP: 192.168.x.x`**. That means it's on your Wi-Fi.
   - `IP: 0.0.0.0` means it hasn't joined. Check the name and password, and
     that it's a 2.4 GHz network, then rebuild and upload again.
   - To watch it connect: **Project Tasks → Monitor** shows lines starting
     `WiFi:`.

## Step 4: Add it in Zeus

Open **Mesh Messenger → Settings → Add node → Scan**. The node appears by
the name you gave it. Press **Add**. You don't need the IP address. Zeus
recognises the node by its identity and finds it again if the address changes.

If Scan doesn't find it, use **Add manually** with the IP from the screen. If
that doesn't work either, the PC can't reach the node. Usually the node is on
a different network or VLAN from the PC, and your router or firewall blocks
TCP port **5000** between them. See [Getting started](getting-started.md).

## Changing settings later

The Wi-Fi firmware has no Bluetooth, so the phone app can't reach it over
Bluetooth any more (if your version of the app offers a Wi-Fi/TCP connection,
that works too: address from the screen, port 5000). Otherwise, there are two
options:

- **meshcore-cli over Wi-Fi** (needs Python): `pip install meshcore-cli`, then,
  with Zeus's connection to the node briefly released (disable the node in
  Zeus, or close Zeus):

  ```powershell
  meshcore-cli -t 192.168.1.50 infos
  meshcore-cli -t 192.168.1.50 set name "KQ4WLR Shack"
  meshcore-cli -t 192.168.1.50 set radio 910.525,62.5,7,5
  meshcore-cli -t 192.168.1.50 get_channels
  ```

  (Use your own IP address and radio settings.)
- **Swap back to Bluetooth for a while:** flash the Bluetooth firmware with
  the web flasher **with Erase device unticked**, change things in the app,
  then upload the Wi-Fi build again from PlatformIO. Settings and identity
  carry across both ways.

## Moving to a new Wi-Fi network

Change the two lines in `platformio.local.ini` and **Upload** again.

## Good to know

- **One app at a time.** A MeshCore node serves one connection. While
  meshcore-cli or anything else is connected, Zeus waits and reconnects
  afterwards.
- **No password on the Wi-Fi connection.** Anything on your network that can
  reach port 5000 can use the node. Keep it on your home or IoT network, never
  port-forwarded to the internet.
- **Messages while Zeus is closed.** The node holds up to 256 messages and
  Zeus collects them when it reconnects. See [Zeus is offline](offline.md).
- **Updating MeshCore later:** `git fetch --tags`, `git checkout` the new
  `companion-v…` tag, then **Upload**. Your `platformio.local.ini` stays put.
  If a newer MeshCore release has renamed the Heltec V4 settings, compare the
  example file with the `[env:heltec_v4_companion_radio_wifi]` section in
  `variants/heltec_v4/platformio.ini`.
