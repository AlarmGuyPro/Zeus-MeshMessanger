# MeshCore companion protocol — what Mesh Messenger uses

Working notes for the MeshCore connector, written from the upstream sources
below. Only the parts this feature uses are listed.

Sources (read 2026-10-05):

- `meshcore-dev/meshcore` @ [`a366955`](https://github.com/meshcore-dev/meshcore/tree/a366955cb2f67b8e6842d4f00d2b6a554dddd88a)
  - `docs/companion_protocol.md` (official, marked "still in development")
  - `examples/companion_radio/MyMesh.cpp`, `main.cpp` (authoritative codes and layouts)
  - `src/helpers/esp32/SerialWifiInterface.cpp`, `src/helpers/BaseChatMesh.cpp`
  - `variants/heltec_v4/platformio.ini`
- `meshcore-dev/meshcore-cli` @ [`a43041f`](https://github.com/meshcore-dev/meshcore-cli/tree/a43041fff80223033f4eaad0f6b5882e3d0677e2) — command set used day to day

Where the doc and firmware disagree, firmware wins (noted below).

## Firmware and Wi-Fi

- Heltec V4 needs the **companion radio Wi-Fi** build
  (`heltec_v4_companion_radio_wifi`, or `heltec_v4_tft_companion_radio_wifi`).
- **The Wi-Fi SSID and password are compile-time settings** (`WIFI_SSID`,
  `WIFI_PWD` build flags). There is no runtime Wi-Fi setup, so the operator
  has to build the firmware with their network details. This is flasher
  territory; our docs just need to say so.
- TCP port **5000** (`TCP_PORT`, overridable at build time).
- That build sets 40 channel slots, 350 contacts and a 256-message offline
  queue (the generic defaults are smaller).

## Transport

- Frames, both directions: `<dir> <len lo> <len hi> <payload>`; length is
  **little-endian**. `dir` is `'<'` (0x3C) app → node and `'>'` (0x3E)
  node → app. Max payload `MAX_FRAME_SIZE` = 176.
- **One client at a time.** When a new TCP client connects, the node drops
  the existing one. If another app (or meshcore-cli) connects, Zeus gets
  disconnected; reconnecting immediately would kick them off in turn. The
  connector must back off and show "another client took the node".
- The node's outbound queue is only 4 frames deep: read continuously.
- Commands are one at a time: send, wait for the matching reply (≈5 s
  timeout, longer for `SET_CHANNEL`), then the next. Pushes (0x80+) can arrive
  at any time.
- All integers little-endian; strings UTF-8.

## Session start

1. `CMD_APP_START` (1): `01` + 7 reserved bytes + app name → `RESP_CODE_SELF_INFO` (5):
   our public key (32), advert lat/lon (int32 / 1e6), radio freq/bw
   (uint32 / 1000), SF, CR, TX power, then **our node name** (needed for the
   channel text limit below).
2. `CMD_DEVICE_QUERY` (22): `16 03` → `RESP_CODE_DEVICE_INFO` (13): firmware
   version code, max contacts (÷2 stored), max channels, build, model, version.
3. `CMD_SET_DEVICE_TIME` (6): uint32 epoch. Node only accepts time moving
   forward (else `ERR_CODE_ILLEGAL_ARG`). Do this on every connect.
4. `CMD_GET_CONTACTS` (4) [optional uint32 `since`] → `CONTACTS_START` (2,
   uint32 total) · `CONTACT` (3) × n · `END_OF_CONTACTS` (4).
5. `CMD_GET_CHANNEL` (31) for each slot → `CHANNEL_INFO` (18).
6. Drain the offline queue: repeat `CMD_SYNC_NEXT_MESSAGE` (10) until
   `NO_MORE_MESSAGES` (10).

## Contact record (`RESP_CODE_CONTACT` 3, `PUSH_CODE_NEW_ADVERT` 0x8A)

`code` · pub_key (32) · type (1: `1` chat, `2` repeater, `3` room, `4`
sensor) · flags (1) · out_path_len (1; `0xFF` = no known path, sends flood)
· out_path (64) · name (32, NUL-padded) · last_advert (uint32) · lat (int32
/1e6) · lon (int32 /1e6) · lastmod (uint32).

## Messages

**Send DM** — `CMD_SEND_TXT_MSG` (2): txt_type (0 plain) · attempt (0..3) ·
timestamp (uint32) · recipient pub-key prefix (6) · text.
**Send to channel** — `CMD_SEND_CHANNEL_TXT_MSG` (3): txt_type (0) · channel
index · timestamp (uint32) · text.

Both reply `RESP_CODE_SENT` (6): flood flag (1 = sent flood, 0 = direct) ·
expected-ack tag (uint32) · suggested timeout (uint32 ms). Failure is
`RESP_CODE_ERR` (1) with `ERR_CODE_TABLE_FULL` (3) (queue full) or
`ERR_CODE_NOT_FOUND` (2) (unknown contact).

**Delivery** — DMs only: `PUSH_CODE_SEND_CONFIRMED` (0x82): ack tag (4) ·
round-trip time (uint32 ms). No confirmation arrives for channel messages.

**Text limits** (firmware, not the doc's "133 characters"):

- `MAX_TEXT_LEN` = 160 bytes for a DM (156 on retry attempts > 3).
- Channel messages are sent as `"<our name>: <text>"` and the firmware
  **silently truncates** to 160 bytes total. So the usable channel limit is
  `160 − (UTF-8 bytes of our node name + 2)`. We enforce it before sending.

**Receive** — `PUSH_CODE_MSG_WAITING` (0x83) means "call
`CMD_SYNC_NEXT_MESSAGE`". Replies we handle (firmware v3+ sends the V3 forms):

| Code | Layout after the code byte |
|---|---|
| `CONTACT_MSG_RECV_V3` (0x10) | SNR (int8 ×4) · 2 reserved · sender pub-key prefix (6) · path_len · txt_type · timestamp (4) · [signature (4) if txt_type = 2] · text |
| `CHANNEL_MSG_RECV_V3` (0x11) | SNR (int8 ×4) · 2 reserved · channel index · path_len · txt_type · timestamp (4) · text |
| `CONTACT_MSG_RECV` (0x07) / `CHANNEL_MSG_RECV` (0x08) | same without the SNR + reserved bytes |
| `NO_MORE_MESSAGES` (0x0A) | — |

Channel text arrives as `"<sender name>: <text>"`; split on the first `": "`
to get the sender (channel messages carry no sender key).

`path_len` on receive: `0xFF` = arrived direct (routed); otherwise the packet
was flooded and the value encodes the hop count (low 6 bits = count of path
hashes, top 2 bits + 1 = hash size). Show hops = low 6 bits for flooded
packets, "direct" for `0xFF`.

## Channels (join / create / leave)

`CMD_GET_CHANNEL` (31): index → `CHANNEL_INFO` (18): index · name (32,
NUL-padded) · secret (16).
`CMD_SET_CHANNEL` (32): index · name (32, NUL-padded) · secret (16) → OK/ERR.
A 32-byte secret is rejected. Free slot = empty name and all-zero secret;
leave = write that back.

Channel kinds (from the doc):

- **Public**: the well-known key `8b3387e9c5cdea6ac9e5edbaa115cd72`.
- **Hashtag** (`#name`): key = first 16 bytes of SHA-256 of the name
  including `#`. Encrypted on air, but anyone who guesses the name can read
  it — treat as public.
- **Private**: random 16-byte secret, shared out of band.

The doc says indexes 0–7; the Heltec V4 Wi-Fi build has 40 slots — use
`max channels` from `DEVICE_INFO`.

## Routing, hops and signal

- **Path discovery** — `CMD_SEND_PATH_DISCOVERY_REQ` (52): `00` · full
  pub key (32). Replies `RESP_CODE_SENT`, then `PUSH_CODE_PATH_DISCOVERY_RESPONSE`
  (0x8D): reserved · pub-key prefix (6) · out_path_len · out_path · in_path_len
  · in_path. Gives the route each way, as path hashes (repeater key prefixes).
- **Trace** — `CMD_SEND_TRACE_PATH` (36): tag (4) · auth (4) · flags (low 2
  bits = path hash size code) · path (list of repeater hash bytes). Reply
  `RESP_CODE_SENT`, then `PUSH_CODE_TRACE_DATA` (0x89): reserved · path_len ·
  flags · tag (4) · auth (4) · path hashes · per-hop SNR (int8 ×4) · final SNR
  (int8 ×4). This is the "SNR at every repeater" view.
- **Reset path** — `CMD_RESET_PATH` (13): pub key (32). Next send floods and
  relearns the route. `PUSH_CODE_PATH_UPDATED` (0x81) + pub key tells us a
  contact's route changed (re-fetch that contact).
- **Radio stats** — `CMD_GET_STATS` (56) + type:
  - `0` core: battery mV (uint16) · uptime s (uint32) · error flags (uint16) · TX queue length (1)
  - `1` radio: **noise floor** (int16 dBm) · **last RSSI** (int8) · **last SNR** (int8 ×4) · TX airtime s (uint32) · RX airtime s (uint32)
  - `2` packets: received · sent · sent flood · sent direct · received flood · received direct · receive errors (uint32 each)
- **Battery** — `CMD_GET_BATT_AND_STORAGE` (20) → (12): mV (uint16) · used KB · total KB.
- **Repeater status** — `CMD_SEND_STATUS_REQ` (27): pub key (32) →
  `RESP_CODE_SENT`, then `PUSH_CODE_STATUS_RESPONSE` (0x87): reserved ·
  pub-key prefix (6) · status blob (repeater stats; layout in
  `docs/stats_binary_frames.md`, to read when we build this).

## Adverts

- `CMD_SEND_SELF_ADVERT` (7): optional byte `1` = flood, else zero-hop. Lets
  others discover us.
- `PUSH_CODE_ADVERT` (0x80) + pub key: known contact re-advertised.
  `PUSH_CODE_NEW_ADVERT` (0x8A) + full contact record: new contact.

## Error codes

`1` unsupported command · `2` not found · `3` table/queue full (retry) ·
`4` bad state · `5` storage I/O · `6` illegal argument.

## Open questions (verify on hardware)

- Exact repeater status blob layout (`stats_binary_frames.md`).
- Whether a node with Wi-Fi companion firmware still advertises over BLE (the
  Wi-Fi build env has no BLE flags, so probably not).
