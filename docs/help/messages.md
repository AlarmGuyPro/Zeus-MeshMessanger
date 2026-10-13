# Messages and conversations

## One inbox, two networks

Every conversation shows a badge — **MT** for Meshtastic, **MC** for
MeshCore — and the node it belongs to. The same channel name or the same
person on both networks shows as two separate conversations, on purpose.

## Replies stay on their network

A reply always goes out on the node the conversation came in on. You can't
switch it, and Mesh Messenger never does it for you. If that node is offline
or removed, the reply fails and shows **Not sent** with the reason. Only a
**New message** asks which node to send from.

## Delivery status

| Shown | Meshtastic | MeshCore |
|---|---|---|
| **Sent** | Your node accepted it | Your node transmitted it (flood or direct) |
| **Delivered** | DM: the recipient acknowledged. Channel: at least one other node relayed it. | DM: the recipient acknowledged, with the round-trip time. |
| No confirmation | — | Channel messages: MeshCore has no acknowledgement for channels, so Sent is final. |
| **Not sent** / **Failed** | With the reason (no route, timeout, too long, node offline…) | With the reason |

## Length limits

LoRa messages are short. The counter counts **bytes**, not characters:
accented letters and emoji take 2–4 bytes each.

| | Limit |
|---|---|
| Meshtastic | 200 bytes |
| MeshCore direct message | 160 bytes |
| MeshCore channel message | 160 bytes minus your node's name and 2 more (MeshCore sends channel text as `YourName: message`) |

Mesh Messenger blocks a message that's too long instead of letting the
firmware cut it off silently.

## The newest message is at the top

A conversation reads like a mail client, not a chat window: the most recent
message is at the top and older traffic is below it. What just arrived is
where you are already looking, and a long thread never pushes it off the
bottom of the screen. Opening a conversation, and sending, both take you to
the top; a message arriving while you are reading older traffic leaves your
place alone.

## Messages that arrived while Zeus was closed

They're marked as such and keep their original time — the block sits below
the traffic from this session, under **Received while Zeus was closed**. See
[When Zeus is closed](offline.md).
