# Channels and rooms

## Channels

A channel is a group chat everyone on it can read. What makes it public or
private is its key.

| Kind | Who can read it | How to join |
|---|---|---|
| **Public** | Anyone | Meshtastic: the default channel. MeshCore: the Public channel. |
| **Hashtag** (MeshCore) | Anyone who knows or guesses the name — the key is made from the name | Type the name, e.g. `#denver` |
| **Private** | Only people you give the key to | MeshCore: the name and its key. Meshtastic: in the Meshtastic app. |

Your node has a fixed number of channel slots (Meshtastic 8, MeshCore up to
40 on the Heltec V4 Wi-Fi build). Joining uses a slot; leaving frees it.
Joining and leaving change the channel list **on your node**, so they apply
even when Zeus is closed. Neither one transmits anything.

## Joining and leaving (MeshCore)

On the **Nodes** tab, under **Channels**:

- **Join a channel** takes a name and, for anything that isn't a `#` name, a
  key of 32 hex characters.
- A name starting with `#` makes its own key out of the name, so everyone who
  types `#denver` is on the same channel without exchanging anything. That is
  what makes hashtag channels open: the name *is* the key.
- Any other channel needs its key from whoever runs it. There is no way to
  work it out from the name, which is the point.
- **Leave…** clears the slot, with a confirmation. Messages already in Zeus
  are kept; you simply stop hearing new ones.

Slot 0 is the node's primary channel and Zeus won't clear it — a node needs
somewhere to put ordinary traffic. Change that in the MeshCore app if you
really mean to.

If the node has no free slot, leave a channel you don't read before joining
another.

**Meshtastic channels are set in the Meshtastic app**, not from Zeus. Zeus
lists them and lets you read and post, but doesn't add or remove them.

## Rooms (MeshCore only)

A **room server** is a MeshCore node that acts as a small shared message
board. Members log in with the room's password; the server keeps the last
32 posts and delivers each new post to every member, including posts made
while a member's node was off.

Use a room as the **"leave a message for the shack"** place: anyone in the
room can post, and your Zeus node collects the posts the next time it logs
in. Every member reads every post, so it's a notice board, not private mail.

Things to know:

- The room keeps posts in memory: if the room server reboots or loses power,
  its posts are gone.
- Posts are up to 151 bytes. A room has up to 20 members.
- If the room can't reach your node 3 times in a row (your node was off or
  out of range), it stops trying until your node logs in again. That's what
  **Rejoin automatically when Zeus connects** is for. It's off by default
  because it transmits a login each time Zeus connects; without it, press
  **Rejoin** yourself after an outage.
- The password is the room's member password. Mesh Messenger never asks for
  a room's or repeater's admin password.
- Setting up a room server (its name, password, whether guests can read) is
  done with the MeshCore tools, not in Zeus.
