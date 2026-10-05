# When Zeus is closed

Your Zeus node keeps running while Zeus is closed, and holds what it receives
until Zeus connects again. How much it can hold depends on the firmware.

| | MeshCore node | Meshtastic node |
|---|---|---|
| Keeps | Direct messages, channel messages, room posts | Every packet, including position and telemetry, which share the space with text |
| How many | 256 (Heltec V4 Wi-Fi build) | 8–32 packets, depending on the board |
| When full | Drops the oldest channel messages first; keeps direct messages | Drops new packets |
| If the node reboots | Lost | Lost |

People who send you a direct message see it as **delivered** as soon as your
node receives it, even if Zeus is closed.

When Zeus starts, it collects everything the node kept and marks it
**received while Zeus was closed**, with the original times. If Zeus can tell
it probably missed messages, it shows a **messages may have been missed**
notice with the time range.

## If the node itself was off

Nothing on your node can help: direct messages to it fail (senders see "not
delivered"), and channel messages are simply not heard.

What can help:

- A **MeshCore room** (see [Channels and rooms](channels-and-rooms.md)) keeps
  the last 32 posts and delivers the ones you missed when your node rejoins.
- A **Meshtastic Store & Forward** server on your mesh can replay recent
  messages if you turn on *Ask Store & Forward for missed messages on
  connect* (see [options](options.md)).
- A **Mesh Gateway** (planned, optional): a small always-on computer such as
  a Raspberry Pi that holds thousands of messages on disk.

## When Zeus restarts

Messages Zeus has already shown are kept across restarts (up to *History to
keep* per conversation).
