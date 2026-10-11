# Testing MeshCore direct messages between two of your own stations

## Why both sides need each other

A MeshCore direct message is encrypted with a key worked out from the
sender's private key and the **recipient's public key**. The recipient
reverses it using their private key and the **sender's public key**, which it
can only do if the sender is in its contact list. A node that receives a
direct message from a station it hasn't saved cannot decrypt it, and drops it
without a word.

So a direct message needs **both** contact lists populated:

| Direction | Zeus node must have | Wio must have |
|---|---|---|
| Zeus → Wio | the Wio | the Zeus node (to read it and reply) |
| Wio → Zeus | the Wio (to read it) | the Zeus node |

That is what the phone app's "you must be in the recipient's address book"
dialog is telling you. Both lists have been full at some point today, so it
is likely neither side ever saved the other.

## Before you start

- Phone app connected to the **Wio** (the Heltec is Wi-Fi only now, so there
  is no clash).
- Zeus open on the Mesh Messenger panel.
- Both radios powered and in the same room.

## Step 1 — check both sides have room

1. **Zeus → Nodes.** Read the line under the table: *"N of 350 contact slots
   used"*. Confirm **Recycle the oldest contact when the list is full** is
   ticked. If the warning banner is still showing, press **Remove** on a few
   repeaters you don't need.
2. **Phone app → the Wio's contact list.** Confirm it isn't full after the
   dozen you removed. If the app shows a count, note it.

A full list on either side will silently undo everything below.

## Step 2 — tidy Zeus so the result is unambiguous

3. **Zeus → Inbox → KQ4WLR Wio L1 → Delete…** and confirm. That thread is
   nothing but failed sends and will make the next steps confusing.

## Step 3 — the Wio learns the Zeus node

4. **Zeus → Nodes → Send advert.** Press it once. (It transmits; once is
   enough.)
5. Wait about 30 seconds, then look at the **Wio's contact list** in the phone
   app. **KQ4WLR Zeus** should appear.
   - If the app is set to add contacts manually, it will be sitting in a
     pending or "new contacts" list — add it from there.
   - If it does not appear at all, the Wio did not hear the advert or had no
     room. Free a slot and repeat step 4.
6. **Mark KQ4WLR Zeus as a favourite** on the Wio if the app offers it (a star
   or similar). Favourites are never recycled, so a future flood of repeaters
   can't evict your Zeus station.

## Step 4 — the Zeus node learns the Wio

7. In the phone app, **send an advert from the Wio** (⋮ menu → Advert / Send
   advert; flood if it offers the choice).
8. **Zeus → Nodes → Re-read from node**, then search the list for `Wio`.
   - Listed with no badge: the node saved it. Good.
   - Listed as **heard · not saved**: also fine — Zeus saves it on the node
     automatically when you send the first message.
   - Not listed at all: the Zeus node did not hear the advert. Repeat step 7.

## Step 5 — test both directions

9. **Wio → Zeus.** In the phone app, open the **KQ4WLR Zeus** contact and send
   "test from wio". It should appear in the Zeus **Inbox** within seconds, as
   a direct conversation, with SNR and "direct".
10. **Reply from Zeus** in that conversation. Expect **Sent**, then
    **Delivered** with a round-trip time of a second or two. The reply should
    arrive on the phone.
11. **Zeus → Wio, from scratch.** **New message → Direct / room → KQ4WLR Wio
    L1**, send "test from zeus". Expect the same Sent → Delivered.

If 9 and 10 work but 11 does not, the Zeus node has the Wio but something is
wrong with the send path. If 9 never arrives, the Wio has not saved the Zeus
node — go back to step 3.

## Step 6 — if anything fails

12. **Zeus → Debug → Copy all**, and paste it. Note which step failed and what
    the phone app said.

Useful lines to look for in the log:

- `Sending an advert…` — step 4 reached the radio.
- `Heard an advert from "KQ4WLR Wio L1"… The node has not saved it` — step 7
  arrived but the node is in manual-add mode or was full.
- `Saving heard station … as a contact on the node.` — Zeus saved it for you.
- `Direct to …: flooded, ack tag …` — the message was handed to the radio.
- `Delivered: ack … after N ms` — the Wio received and acknowledged it.

A send that reaches `ack tag` but never reaches `Delivered` means the Wio
heard nothing or could not decrypt it, which again points at the Wio's
contact list.
