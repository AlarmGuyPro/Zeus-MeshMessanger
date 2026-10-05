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
| Remove node | Disconnects and forgets the node. Its conversations stay in history but can no longer be replied to (replies never move to another node). | No | — |

On every connect Zeus also sets a MeshCore node's clock from the PC clock
(MeshCore needs a correct clock for message timestamps). This is a setting on
the node itself, not a transmission.

## Messages

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Send / Reply | Sends your message on the conversation's own node. | When you press it | — |
| Mute conversation | Stops unread counts and highlights for that conversation. Messages still arrive. | No | Off |
| History to keep | How many messages Zeus keeps per conversation, across restarts. Older ones are removed from Zeus only, not from anyone else. | No | 500 |

## Channels

| Option | What it does | Transmits | Default |
|---|---|---|---|
| Join public / hashtag channel | Adds the channel to a free slot on your node. Anyone who knows (or guesses) a hashtag name can read it. | No | — |
| Join private channel (link or key) | Adds a private channel from a share link (Meshtastic) or name + key (MeshCore). For Meshtastic links, only the channel is added: radio settings in the link are ignored, and you're warned if they differ from your node's. | No | — |
| Create private channel | Makes a new random key and shows a link / QR code to share. | No | — |
| Show key | Displays the channel key so you can share it. Anyone with the key can read the channel. | No | Hidden |
| Leave channel | Frees the slot on your node. You stop receiving it. | No | — |

Joining or leaving changes your **node's** channel list, so your node stops or
starts hearing that channel even when Zeus is closed.

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
