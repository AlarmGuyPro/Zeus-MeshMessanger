# Mesh Messenger help

Mesh Messenger lets you read and send Meshtastic and MeshCore messages from
inside Zeus, through a LoRa node dedicated to the station.

- [Getting started](getting-started.md) — the node, connecting it, first message
- [Messages and conversations](messages.md) — the inbox, replies, delivery ticks, length limits
- [Channels and rooms](channels-and-rooms.md) — public, hashtag and private groups; MeshCore room servers
- [Reading signal and hops](signal.md) — what SNR, RSSI and hops tell you
- [Tools](tools.md) — traceroute, path discovery, trace, adverts, repeater status
- [When Zeus is closed](offline.md) — what your node keeps for you, and what it can't
- [Options reference](options.md) — **every setting: what it does, whether it transmits, its default**

## Two rules worth knowing first

1. **A reply always goes out on the network and node the conversation came
   from.** A MeshCore conversation is never answered over Meshtastic, or the
   other way round. If that node is offline, the reply fails and says so.
2. **Mesh Messenger only transmits when you ask it to**, with the exceptions
   you switch on yourself (listed in the [options reference](options.md)
   under "Transmits automatically"). It never changes your node's radio
   settings and never touches the Zeus transceiver.
