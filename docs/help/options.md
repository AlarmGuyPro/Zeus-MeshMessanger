# Options reference

Every setting in Mesh Messenger, what it does, and whether it makes your node
transmit. "Transmits" matters because airtime on a LoRa mesh is shared by
everyone: each transmission from your node also gets repeated by repeaters,
so it costs the whole mesh airtime, not just yours.

**Transmits** column:

- **No** — local to Zeus, or only reads from your node over Wi-Fi.
- **When you press it** — transmits once, only when you click.

Nothing in Mesh Messenger transmits on its own. There is no setting that can
make it: every transmission in the list below happens because you clicked
something.

This page describes what the panel actually does. Anything not here is not in
this version — see **[Not in this version](#not-in-this-version)** at the end.

## Nodes

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Network | Meshtastic or MeshCore. Fixed per node; it decides which conversations belong to the node. | No | — (you choose) |
| Name in Zeus | What Zeus shows for this node. Does not rename the node on the mesh (do that in the native app). | No | node's own name |
| Scan | Looks for Meshtastic and MeshCore nodes on your network and on the extra networks below, and lists them by name. Checks each one really is a mesh node before showing it. Scanning can briefly disconnect a phone or meshcore-cli that is using a *different* MeshCore node over Wi-Fi. | No (network only, nothing on the mesh) | — |
| Extra networks to scan | Other VLANs or address ranges to include, e.g. `192.168.30.0/24` or `10.0.20.10-10.0.20.80`. Local addresses only, up to 4,094 addresses each (a /20). | No | none |
| Address | Where the node was last found (IP or host name). Filled in by Scan; you can type one instead. Addresses outside your own network, and addresses on the Zeus computer itself, are refused. | No | found by Scan |
| Find this node automatically if its address changes | If the node stops answering, Zeus checks its host name, then (Meshtastic) its network announcement, then scans the network it was last on and your extra networks, and reconnects when it finds the *same* node. Retries slowly, never in a tight loop, and never follows a node to an address outside your network. | No (network only) | **On** |
| Re-pair | Zeus remembers each node's identity and refuses a different node at the same address. Use Re-pair after re-flashing or replacing the board. | No | — |
| Port | TCP port. Meshtastic 4403, MeshCore 5000 unless your firmware was built differently. | No | 4403 / 5000 |
| Enabled | Off disconnects Zeus from the node without deleting its conversations. | No | On |
| Recycle the oldest contact when the list is full (MeshCore) | A setting on the node. With it on, the node drops its oldest non-favourite contact to make room for a new one, so a full list never blocks new stations. With it off, a full list stays full and direct messages to new stations fail. | No | Off (the node's own default) |
| Remove (a contact, MeshCore) | Deletes that contact from the node's list, freeing a slot. | No | — |
| ☆ (a contact, MeshCore) | Marks the contact a favourite on the node. Favourites are never recycled and never swept up by **Tidy the list**. | No | Off |
| Tidy the list (MeshCore) | Clears a lot of contacts off the node at once: every repeater, or every station not heard in 7, 30 or 90 days. Zeus counts them and asks before removing anything. Favourites, room servers, and any station whose age the node doesn't report are kept. A few hundred takes a minute or two: they go one at a time, paced so the node keeps up. | No | 30 days |
| Show repeaters | Repeaters are hidden from the list by default because a busy mesh has hundreds. Search always finds them whether or not this is ticked. | No | Off |
| Re-read from node | Asks the node for its contact or node list again. Useful after adding a contact in the phone app. | No | — |
| Send advert (MeshCore) | Announces this node on the air so other stations can add it and message you. This is the only button on the tab that transmits. | When you press it | — |
| Search this list | Filters the list by name, short name, type or key. Local to Zeus. | No | — |
| Remove node | Disconnects and forgets the node. Its conversations stay in history but can no longer be replied to (replies never move to another node). | No | — |

On every connect Zeus also sets a MeshCore node's clock from the PC clock
(MeshCore needs a correct clock for message timestamps). This is a setting on
the node itself, not a transmission.

Zeus remembers up to 300 MeshCore stations it has heard but that the node has
not saved, and up to 600 Meshtastic stations, forgetting whichever it heard
from longest ago. That is Zeus's own list; your node's contacts are untouched,
and a station comes straight back when it is heard again.

## Messages

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Send / Reply | Sends your message on the conversation's own node. | When you press it | — |
| Mute conversation | Stops unread counts and highlights for that conversation. Messages still arrive. | No | Off |
| Search conversations | Filters the list by title, sender or message text, matching anywhere in the text. Local to Zeus. | No | — |
| All / Direct / Channels | Folders for the conversation list. Channels your node has joined are listed even before anyone talks on them. | No | All |
| Send again | Sends a message that never went out, as a new transmission. The failed entry is replaced. | When you press it | — |
| Clear them | Removes the messages in this conversation that never went out. Removed from Zeus only. | No | — |
| Delete… | Forgets the conversation and its messages. Removed from Zeus only; the other station keeps its copy. | No | — |
| Messages kept per conversation | How many messages Zeus keeps per conversation, across restarts. Older ones are removed from Zeus only, not from anyone else. | No | 500 |

History is also bounded in size, whatever that setting says: 192 KB for one
conversation and 4 MB in all. Past that, Zeus forgets the oldest messages, and
then the conversations heard from longest ago. The **Logs** tab shows how much
is held. The point of the budget is that history can never grow large enough
for a save to fail and take the lot with it.

## Channels

MeshCore only: Meshtastic channels are set in the Meshtastic app, and Zeus
lists them without changing them.

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Join a channel: name | A name starting with `#` makes its own key from the name, so anyone who types the same name is on the same channel. Case matters: `#test` and `#Test` are different channels with different keys. | No | — |
| Join a channel: key | 32 hex characters. Required for any name that doesn't start with `#`; ignored for one that does. | No | — |
| Leave… | Frees the slot on your node, after a confirmation. You stop receiving that channel; messages already in Zeus are kept. | No | — |

Joining or leaving changes your **node's** channel list, so your node stops or
starts hearing that channel even when Zeus is closed. Slot 0 is the node's
primary channel and can't be cleared from Zeus.

Channel keys are held on the node, not in Zeus. If you re-flash the board you
will need to join your channels again.

## Logs

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Activity log | What each node and Zeus have done recently: connects, commands, replies, the channels the node holds, every message in and out, adverts heard, acknowledgements and errors. Message text is never logged — only its size and how it arrived. Kept in memory only, so it starts empty each time Zeus opens. | No | — |
| Everything / Messages / Problems | Narrows a busy log. *Messages* keeps only what went out and came in; *Problems* only warnings and errors. | No | Everything |
| Search the log | Filters the log lines. Local to Zeus. | No | — |
| Copy all | Copies every node's details and the whole log to the clipboard, for a bug report. | No | — |

## Not in this version

These are deliberately absent, not hidden somewhere:

- **Rooms.** A MeshCore room server's posts are filed into their own
  conversation when they arrive, but Zeus cannot log into a room, so it will
  not collect posts you missed, and there is no room password anywhere in the
  panel.
- **Route tools** — Meshtastic traceroute, MeshCore path discovery, trace and
  reset path.
- **Repeater and node health** — battery, airtime, noise floor, packet counts,
  repeater status.
- **Meshtastic Store & Forward.**
- **Meshtastic channel management.** Zeus lists Meshtastic channels and will
  not change them.
- **A contact list of Zeus's own.** Your node is the only place contacts and
  channel keys live.

Firmware updates, Wi-Fi setup, region, frequency, preset, power, device role,
keys, factory reset, module settings and remote administration are left to the
official Meshtastic and MeshCore apps and tools, and are not planned here.
