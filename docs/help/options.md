# Options reference

Every setting in Mesh Messenger, what it does, and whether it makes your node
transmit. "Transmits" matters because airtime on a LoRa mesh is shared by
everyone: each transmission from your node also gets repeated by repeaters,
so it costs the whole mesh airtime, not just yours.

**Transmits** column:

- **No** — local to Zeus, or only reads from your node over Wi-Fi.
- **When you press it** — transmits once, only when you click.
- **Automatically** — transmits without a click. Every one of these is **off
  until you turn it on**.

## Nodes

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Network | Meshtastic or MeshCore. Fixed per node; it decides which conversations belong to the node. | No | — (you choose) |
| Name | What Zeus shows for this node. Does not rename the node on the mesh (do that in the native app). | No | node's own name |
| Scan | Looks for Meshtastic and MeshCore nodes on your network and on the extra networks below, and lists them by name. Checks each one really is a mesh node before showing it. Scanning can briefly disconnect a phone or meshcore-cli that is using a *different* MeshCore node over Wi-Fi. | No (network only, nothing on the mesh) | — |
| Extra networks to scan | Other VLANs or address ranges to include, e.g. `192.168.30.0/24` or `10.0.20.10-10.0.20.80`. Local addresses only, up to 4,094 addresses each (a /20). | No | none |
| Address | Where the node was last found (IP or host name). Filled in by Scan; you can type one instead. Public internet addresses are refused. | No | found by Scan |
| Find this node automatically if its address changes | If the node stops answering at its address, Zeus checks its host name, then (Meshtastic) its network announcement, then scans the network it was last on and your extra networks, and reconnects when it finds the *same* node. Retries slowly, never in a tight loop. If you've saved a room or repeater password for this node, Zeus asks first (**Use / Ignore**) instead of switching on its own. | No (network only) | **On** |
| Re-pair | Zeus remembers each node's identity and refuses a different node at the same address. Use Re-pair after re-flashing or replacing the board. | No | — |
| Port | TCP port. Meshtastic 4403, MeshCore 5000 unless your firmware was built differently. | No | 4403 / 5000 |
| Enabled | Off disconnects Zeus from the node without deleting its conversations. | No | On |
| Recycle the oldest contact when the list is full (MeshCore) | A setting on the node. With it on, the node drops its oldest non-favourite contact to make room for a new one, so a full list never blocks new stations. With it off, a full list stays full and direct messages to new stations fail. | No | Off (the node's own default) |
| Remove (a contact, MeshCore) | Deletes that contact from the node's list, freeing a slot. | No | — |
| ☆ (a contact, MeshCore) | Marks the contact a favourite on the node. Favourites are never recycled and never swept up by a tidy-up. | No | Off |
| Remove all repeaters (MeshCore) | Clears every repeater off the node in one go. Counts them and asks first. Favourites are kept. | No | — |
| Remove any not heard in 7 / 30 / 90 days (MeshCore) | Clears stations that have gone quiet. Counts them and asks first. Favourites, room servers, and any station whose age the node doesn't report are kept. | No | 30 days |
| ☆ Favourite (a contact, MeshCore) | Marks the contact on the node so recycling never drops it. Use it for the stations you actually message. | No | Off |
| Show repeaters | Repeaters are hidden from the Nodes list by default because a busy mesh has hundreds. Search always finds them whether or not this is ticked. | No | Off |
| Re-read from node | Asks the node for its contact or node list again. Useful after adding a contact in the phone app. | No | — |
| Send advert (MeshCore) | Announces this node on the air so other stations can add it and message you. | When you press it | — |
| Search this list | Filters the Nodes list by name, short name, type or key. Local to Zeus. | No | — |
| Remove node | Disconnects and forgets the node. Its conversations stay in history but can no longer be replied to (replies never move to another node). | No | — |

On every connect Zeus also sets a MeshCore node's clock from the PC clock
(MeshCore needs a correct clock for message timestamps). This is a setting on
the node itself, not a transmission.

## Messages

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Send / Reply | Sends your message on the conversation's own node. | When you press it | — |
| Mute conversation | Stops unread counts and highlights for that conversation. Messages still arrive. | No | Off |
| Search conversations | Filters the list by title, sender or message text, matching anywhere in the text. Local to Zeus. | No | — |
| All / Direct / Channels / Rooms | Folders for the conversation list. Channels your node has joined are listed even before anyone talks on them. | No | All |
| Send again | Sends a message that never went out, as a new transmission. The failed entry is replaced. | When you press it | — |
| Clear them | Removes the messages in this conversation that never went out. Removed from Zeus only. | No | — |
| Delete… | Forgets the conversation and its messages. Removed from Zeus only; the other station keeps its copy. | No | — |
| History to keep | How many messages Zeus keeps per conversation, across restarts. Older ones are removed from Zeus only, not from anyone else. | No | 500 |

## Channels

MeshCore only: Meshtastic channels are set in the Meshtastic app, and Zeus
lists them without changing them.

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Join a channel: name | A name starting with `#` makes its own key from the name, so anyone who types the same name is on the same channel. | No | — |
| Join a channel: key | 32 hex characters. Required for any name that doesn't start with `#`; ignored for one that does. | No | — |
| Leave… | Frees the slot on your node, after a confirmation. You stop receiving that channel; messages already in Zeus are kept. | No | — |

Joining or leaving changes your **node's** channel list, so your node stops or
starts hearing that channel even when Zeus is closed. Slot 0 is the node's
primary channel and can't be cleared from Zeus.

## Rooms (MeshCore room servers)

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Join room | Logs your node into the room with its member password, once. The password is saved in Zeus and never shown again. | When you press it | — |
| **Rejoin automatically when Zeus connects** | Each time Zeus connects to the node, it logs into this room again so the room sends you the posts you missed. Needed because a room stops sending to a member after 3 failed attempts (for example while your node was off). Logins are spaced out and never retried in a loop. | **Automatically** (one login per room per connect) | **Off** |
| Post | Sends your post to the room; everyone in the room receives it. | When you press it | — |
| Leave room | Forgets the password and stops automatic rejoining. The room may keep you on its member list until it expires you. | No | — |

Without automatic rejoin, press **Rejoin** on the room after your node has
been off to collect missed posts.

## Tools

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Traceroute (Meshtastic) | Asks the mesh for the route to a node and the signal at each hop, both ways. | When you press it | — |
| Path discovery (MeshCore) | Finds the current route to a contact and back. | When you press it | — |
| Trace (MeshCore) | Sends a trace along a known route and reports the signal at every repeater. | When you press it | — |
| Reset path (MeshCore) | Forgets the stored route to a contact; your next message floods and the route is relearned. | No (the next message does) | — |
| Send advert (MeshCore) | Announces your node so others can add it. *Zero-hop* reaches only nodes in direct range; *flood* goes across the mesh (more airtime). | When you press it | — |
| Tool cooldown | Minimum time between route tools, to protect shared airtime. | — | 30 s |

## Debug

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Activity log | Shows what each node and Zeus have done recently: connects, commands, replies, acknowledgements and errors. Message text is never logged. Kept in memory only, so it starts empty each time Zeus opens. | No | — |
| Search the log | Filters the log lines. Local to Zeus. | No | — |
| Copy all | Copies every node's details and log to the clipboard, for a bug report. | No | — |

## Health

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Node health | Battery, airtime, noise floor and packet counts read from your own node. | No | Shown |
| Repeater status → Check now | Logs into a repeater as a guest (never with its admin password) and asks for its status. | When you press it | — |
| Repeater guest password | Only if the repeater has one. Saved, never shown. Leave blank if the repeater's guest password is blank. | No | Blank |
| **Refresh repeater status automatically** | Re-checks the repeater on a timer. | **Automatically** (one request per interval) | **Off** |
| Refresh interval | How often, when the above is on. Can't be set below 15 minutes. | — | 60 min |

## Meshtastic Store & Forward

| Option | What it does | Transmits | Default |
|---|---|---|---|
| **Ask Store & Forward for missed messages on connect** | If your mesh has a Store & Forward server, Zeus asks it for messages from while you were away and adds any it didn't already have. Does nothing useful on meshes without such a server. | **Automatically** (one request per connect) | **Off** |

## Not in Mesh Messenger

Firmware updates, Wi-Fi setup, region / frequency / preset / power, device
role, keys, factory reset, module settings and remote administration are left
to the official Meshtastic and MeshCore apps and tools.
