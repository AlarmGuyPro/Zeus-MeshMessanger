# Tools

Every tool that transmits runs only when you press it, and route tools share
a cooldown (30 s by default) because they use shared airtime. Mesh Messenger
also never sends two things at once: if both your nodes have something to
send, they take turns.

## Meshtastic: Traceroute

Asks the mesh for the route to a node. You get the list of nodes it passed
through each way, with the SNR at every hop. Firmware limits how often
traceroutes are allowed; if one is refused, wait and try again.

## MeshCore: Path discovery, Trace, Reset path

- **Path discovery** finds the route your node uses to reach a contact and
  the route back, as a list of repeaters.
- **Trace** sends a test packet along a route and reports the SNR at each
  repeater and on the final hop — the best way to find the weak link.
- **Reset path** forgets the stored route to a contact. Use it when messages
  to someone keep failing (a repeater moved or went down). Nothing is sent
  until your next message, which then floods and relearns the route.

## MeshCore: Send advert

Announces your node so others can find and add it.

- **Zero-hop**: only nodes in direct radio range hear it.
- **Flood**: repeated across the mesh — use sparingly.

## Repeater status (Health tab)

For a repeater you choose (typically your own), **Check now** logs in as a
guest and fetches its battery, noise floor, last RSSI/SNR, packet counts,
duplicates, airtime and uptime. Mesh Messenger never uses a repeater's admin
password. Automatic refresh is optional and off by default (see
[options](options.md)).
