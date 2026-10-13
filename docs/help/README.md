# Mesh Messenger help

Mesh Messenger lets you read and send Meshtastic and MeshCore messages from
inside Zeus, through a LoRa node dedicated to the station.

- [Getting started](getting-started.md) — the node, connecting it, first message
- [Set up a Heltec V4 for MeshCore](setup-meshcore-heltec-v4.md) — step by step: flash, configure, put it on Wi-Fi
- [Set up a Heltec V4 for Meshtastic](setup-meshtastic-heltec-v4.md) — the same for Meshtastic, plus what to do when Wi-Fi won't connect
- [Messages and conversations](messages.md) — the inbox, replies, delivery ticks, length limits
- [Channels](channels.md) — public, hashtag and private groups, and what Zeus does with a MeshCore room
- [Reading signal and hops](signal.md) — what SNR, RSSI and hops tell you
- [When Zeus is closed](offline.md) — what your node keeps for you, and what it can't
- [Testing direct messages](direct-messages-test.md) — step by step, when a direct message won't go
- [Finding and fixing problems](logs.md) — the Logs tab, tidying up stale messages
- [Options reference](options.md) — **every setting: what it does, whether it transmits, its default**

## Two rules worth knowing first

1. **A reply always goes out on the network and node the conversation came
   from.** A MeshCore conversation is never answered over Meshtastic, or the
   other way round. If that node is offline, the reply fails and says so.
2. **Mesh Messenger only transmits when you ask it to.** Nothing in it
   transmits on a timer or on its own, and there is no setting that makes it.
   Every button that puts something on the air is marked, and listed in the
   [options reference](options.md). It never changes your node's radio
   settings and never touches the Zeus transceiver.
