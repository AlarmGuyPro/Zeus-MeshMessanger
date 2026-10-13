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
board: members log in with the room's password, and the server delivers each
new post to every member.

**Joining a room is not in this version.** If your node is already a member of
a room — because it was logged in with the MeshCore app or meshcore-cli — the
posts it receives appear in Zeus as their own conversation, and you can post
back to it. What Zeus cannot do is log in, so:

- It will not collect the posts you missed while your node was off. A room
  stops sending to a member after three failed attempts, and only a fresh
  login restarts it; use the MeshCore app for that.
- There is no room password anywhere in the panel. Mesh Messenger never asks
  for a room's or a repeater's password.

Setting up a room server — its name, password, whether guests can read — is
done with the MeshCore tools, and so is logging into one.
