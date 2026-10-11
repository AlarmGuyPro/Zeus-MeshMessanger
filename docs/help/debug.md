# Finding and fixing problems

## The Debug tab

**Debug** shows what each node and Zeus have actually done: connects, commands
sent to the node, its replies, acknowledgements and errors, newest first. It
also shows each node's firmware, model, radio settings and how many contacts
and channels it has.

- **Message text is never logged.** The log records that a message of N bytes
  was sent to a station, not what it said.
- The log is kept in memory only. It starts empty each time Zeus opens, and
  nothing is written to disk.
- **Search the log** filters the lines; try `advert`, `ack` or `failed`.
- **Copy all** puts everything on screen on the clipboard. Paste it into a bug
  report — it is the single most useful thing to include.

Reading the log: `TX` is something Zeus asked the node to transmit, `RX` is
something the node reported hearing, `!` is a warning and `✕` an error.

## A message says it wasn't sent

The reason is shown under the message, and **Send again** retries it. Common
ones:

| What it says | What it means |
|---|---|
| The node had no free packet buffer | The node is busy on the air and had nowhere to put the message. Wait a few seconds and send again. |
| This station isn't in your node's contacts | Your node has never heard this station. Ask them to send an advert, then try again. |
| The node's contact list is full | Remove some contacts in the MeshCore app. |
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

A node can be set to add contacts **manually** (the MeshCore app's setting).
Then an advert it hears shows as **heard · not saved**: the node knows the
station but won't message it yet. Zeus saves such a station on the node
automatically the first time you message it, so you don't have to do anything.

Meshtastic nodes announce themselves regularly on their own, so their list
fills in without any action.

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
