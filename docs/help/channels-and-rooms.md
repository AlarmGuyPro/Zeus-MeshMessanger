# Channels and rooms

## Channels

A channel is a group chat everyone on it can read. What makes it public or
private is its key.

| Kind | Who can read it | How to join |
|---|---|---|
| **Public** | Anyone | Meshtastic: the default channel. MeshCore: the Public channel. |
| **Hashtag** (MeshCore) | Anyone who knows or guesses the name — the key is made from the name | Type the name, e.g. `#denver` |
| **Private** | Only people you give the key to | Paste a share link (Meshtastic) or the name and key (MeshCore) |

Your node has a fixed number of channel slots (Meshtastic 8, MeshCore up to
40 on the Heltec V4 Wi-Fi build). Joining uses a slot; leaving frees it.
Joining and leaving change the channel list **on your node**, so they apply
even when Zeus is closed. Neither one transmits anything.

**Meshtastic share links** can also carry radio settings (region, preset).
Mesh Messenger only adds the channels and ignores those settings; if the
link's preset differs from your node's, you're warned, because you won't hear
that group until the presets match (change that in the Meshtastic app if you
really want to).

**Sharing a private channel:** *Show key* displays the link or key and a QR
code. Anyone who has it can read and post to the channel.

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
