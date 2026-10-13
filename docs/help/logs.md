# Finding and fixing problems

## The Logs tab

**Logs** shows what each node and Zeus have actually done: connects, commands
sent to the node, its replies, the channels the node holds, every message in
and out, adverts heard, acknowledgements and errors, newest first. It also
shows each node's firmware, model, radio settings and how many contacts and
channels it has.

- **Message text is never logged.** A line records that a message of N bytes
  arrived on a named channel, or went out to a station — never what it said.
- The log is kept in memory only. It starts empty each time Zeus opens, and
  nothing is written to disk.
- **Everything / Messages / Problems** narrows a busy log: *Messages* keeps
  only what went out and what came in, *Problems* only warnings and errors.
- **Search the log** filters the lines; try `advert`, `ack`, `channel` or
  `failed`.
- **Copy all** puts the whole log on the clipboard, whatever is being shown.
  Paste it into a bug report — it is the single most useful thing to include.

Reading the log: `TX` is something Zeus asked the node to transmit, `RX` is
something the node reported hearing, `!` is a warning and `✕` an error.

The tab also shows how much history Zeus is holding against its own budget.
Past that budget the conversations heard from longest ago are forgotten, so
history can never grow until a save fails.

### Was it received, or did Zeus lose it?

Every arrival is logged, which splits that question cleanly. Send a message on
a channel from another station and look for a line like

```text
RX  Channel 1 ("#Test"): received 24 bytes, SNR 7.5 dB, 2 hops.
```

If it is there, the node decrypted the message and Zeus filed it; if the
conversation looks empty, the fault is in Zeus. If there is no such line, the
message never reached the node in a form it could read — on a channel that
almost always means the two ends do not hold the same key. Channel names are
case-sensitive, and a `#name` channel's key is derived from the name exactly as
typed, so `#test` and `#Test` are two different channels that cannot hear each
other.

## A message says it wasn't sent

The reason is shown under the message, and **Send again** retries it. Common
ones:

| What it says | What it means |
|---|---|
| The node had no free packet buffer | The node is busy on the air and had nowhere to put the message. Wait a few seconds and send again. |
| This station isn't in your node's contacts | Your node has never heard this station. Ask them to send an advert, then try again. |
| The node's contact list is full | The node can only hold so many stations and has no room for this one. See below. |
| No acknowledgement arrived | It went out, but nothing confirmed it. On a channel that is normal; on a direct message the other station may be off or out of range. |
| … is not connected | Zeus has lost its Wi-Fi connection to your own node. Check the Nodes tab. |

**Clear them** removes the messages that never went out, and **Delete…**
forgets a whole conversation. Both remove messages from Zeus only — the other
station keeps its copy, and nothing is sent.

## Nobody appears in the contact list

MeshCore nodes learn stations from **adverts**: a station announces itself,
and nodes that hear it list it. So:

- To message someone, your node must have heard **their** advert. Ask them to
  send one from their app.
- For them to message you, they must have heard **yours**. Press **Send
  advert** on the Nodes tab. This is the one button there that transmits.
- **Re-read from node** re-reads the list from the node, which helps after
  adding a contact in the phone app.

## Contacts I removed have come back

Most MeshCore nodes are set to add every station they hear, by themselves. On a busy mesh
that is hundreds of repeaters, arriving all day, so a list cleared with **Tidy the list**
refills within minutes and looks as though the removal never worked.

The Nodes tab says so when it is happening: **"This node adds stations by itself"**, with
a count of how many it has saved since Zeus connected. That is not something Zeus read from
a setting — it is counted from the node announcing each one, so when it says this, it is
what the node is doing.

Change it under **What this node saves by itself**, on the same tab: set it to *Save only
the kinds I choose* and untick **Repeaters**, or give it a hop limit of 1 or 2 so it only
saves stations you can actually reach. Then one tidy-up will stick.

Both settings are needed together, and Zeus handles that for you: a node set to save
everything ignores the per-kind choices entirely, so unticking Repeaters on its own would do
nothing. That is why the list of kinds is hidden until you choose to be selective.

If you would rather leave it saving everything, turn on **Recycle the oldest contact when
the list is full** and a full list stops mattering.

## The contact list is full

A MeshCore node holds a fixed number of contacts (350 in the build from the
setup guide). On a busy mesh it adds **every** station it hears, so the list
fills up on its own — and once full it cannot save a new station, so direct
messages to anyone it hasn't already saved fail.

The Nodes tab shows how many slots are used. Three ways to keep it healthy:

- **Recycle the oldest contact when the list is full** — a setting on the node.
  With it on, the node drops its oldest non-favourite contact to make room, so
  the list never blocks a new station again. This is the one to use.
- **Remove** next to any contact deletes it from the node, freeing a slot.
- **Tidy the list** clears out a lot at once: every repeater, or every station
  not heard in 7, 30 or 90 days. Zeus counts them and asks before removing
  anything. Favourites and room servers are never included, and neither is a
  station whose age the node doesn't know.

  Clearing a few hundred contacts takes a minute or two. The node writes its
  contact list to flash, so Zeus removes them one at a time with a pause
  between, and reads the list back once at the end rather than after every
  removal. Leave Zeus open and watch the count go down. If the node falls
  behind and stops answering, Zeus stops, tells you how many it managed, and
  stays connected — run it again when the node has caught up.
- **☆ next to a contact** makes it a favourite. Recycling never drops a
  favourite, so star the stations you actually message and they will survive
  any amount of repeater traffic.

None of these transmit anything.

With recycling on, a full list is normal and harmless: the node simply drops
its oldest non-favourite contact when it needs room. Zeus only warns about a
full list when recycling is **off**, because that is the case that breaks
messaging.

A node can also be set to add contacts **manually** (the MeshCore app's setting).
Then an advert it hears shows as **heard · not saved**: the node knows the
station but won't message it yet. Zeus saves such a station on the node
automatically the first time you message it, so you don't have to do anything.

Meshtastic nodes announce themselves regularly on their own, so their list
fills in without any action.

## A direct message needs both sides

MeshCore encrypts a direct message with the recipient's public key, and the
recipient decrypts it using the **sender's** public key — which it can only do
if the sender is in its contact list. A node that receives a direct message
from a station it hasn't saved cannot read it and drops it silently.

So both nodes must hold each other as contacts. If a message goes out and is
never answered, check the **other** station's contact list as well as yours.
There is a full walkthrough in
[Testing direct messages](direct-messages-test.md).

One more trap: adverts carry the sender's own timestamp, and a node ignores an
advert that isn't newer than the one it already stored for that station. If a
stored timestamp is wrong — the Updated column's tooltip will say when a
station's clock is ahead — new adverts from it are discarded as replays and
the contact never updates. Removing the contact clears the stored timestamp so
the next advert is accepted.

## Zeus can't reach the node

The Nodes bar shows the reason. From a command prompt on the Zeus PC:

```powershell
Test-NetConnection 192.168.1.50 -Port 5000
```

(Port 5000 for MeshCore, 4403 for Meshtastic.) `TcpTestSucceeded : False`
means the PC can't reach the node at all — usually a different VLAN, or a
firewall between them. If the node is on another network, add that range under
**Settings → Extra networks to scan**.

A MeshCore node serves **one** connection at a time. If meshcore-cli or
another tool is connected, Zeus waits and reconnects when it lets go.

Careful with ping as evidence: a router or access point can answer ICMP on
behalf of a client it still has in its table, so a node that is **not** on the
network can still reply. If ping succeeds but the port doesn't open, believe
the port. For a Meshtastic node that won't join Wi-Fi at all, work through
[Set up a Heltec V4 for Meshtastic](setup-meshtastic-heltec-v4.md), which
covers reading the node's own serial log and what each failure reason means.
