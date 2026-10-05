# Reading signal and hops

Each received message has a line like `SNR 6.5 dB · RSSI −98 dBm · 2 hops`.
These describe the **last hop only** — the final radio link into your node —
not the whole path.

- **SNR** (signal-to-noise ratio): how far the signal was above the noise.
  LoRa decodes well below the noise floor, so negative values are normal.
  Roughly: above 0 dB is solid, −5 to −10 dB is working, below about −15 dB
  is at the edge (the exact edge depends on your mesh's settings).
- **RSSI**: received signal strength in dBm. Useful for comparing, but SNR
  tells you more about whether a link is reliable. MeshCore messages carry
  SNR only.
- **Hops**: how many repeaters relayed the message. *Direct* means it came
  without being relayed (or, on MeshCore, along a known route). On Meshtastic,
  very old firmware doesn't report hops; that shows as *unknown*, not
  *direct*.
- **via MQTT** (Meshtastic): the message crossed the internet somewhere;
  its signal figures describe only the radio part near you.

## Your node's health

- **Noise floor**: the background noise your node hears, in dBm. A rise
  usually means local interference (check the shack — switching supplies,
  Wi-Fi, the PC).
- **Channel utilisation / airtime**: how busy the channel is. High values
  mean more collisions and slower delivery for everyone.
- **Packet counters**: received, sent, bad (failed to decode) and duplicates.
  Lots of bad packets with a good SNR often points to interference.
