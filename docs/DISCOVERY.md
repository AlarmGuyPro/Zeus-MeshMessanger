# Finding nodes automatically

Goal: an operator should never need to know a node's IP address or set a
static lease. Zeus finds Meshtastic and MeshCore nodes on the LAN and on other
VLANs, remembers *which* node it paired with, and finds it again if its
address changes.

## What each firmware offers

| | Meshtastic | MeshCore (companion Wi-Fi) |
|---|---|---|
| mDNS service | `_meshtastic._tcp` on port 4403, TXT `id` (node id `!xxxxxxxx`), `shortname`, `pio_env` | none |
| DHCP host name | `Meshtastic-XXYY` (last two MAC bytes) | ESP32 default (not set by MeshCore) |
| TCP port | 4403 | 5000 |
| Identity on connect | `my_info.my_node_num` (+ node id) | `SELF_INFO` public key + node name |
| Side effect of a connect | none known (verify) | **drops whatever app is connected** — the node serves one client and a new connection replaces it |

(Meshtastic: `firmware/src/mesh/wifi/WiFiAPClient.cpp`; MeshCore:
`src/helpers/esp32/SerialWifiInterface.cpp`, no mDNS anywhere in the tree.)

## Three ways Zeus finds nodes

1. **mDNS browse (Meshtastic only, same VLAN).** Listens for
   `_meshtastic._tcp`. Instant, no probing. Doesn't cross VLANs unless the
   router reflects mDNS (UniFi "mDNS", Avahi reflector, pfSense/OPNsense
   relay), so it is a fast path, not the only path.
2. **Network scan (both firmwares, any reachable VLAN).** Probes TCP 4403 and
   5000 across:
   - the Zeus PC's own subnets (detected automatically; private ranges only;
     a network larger than /22 is limited to the /24 around the PC), and
   - **extra ranges the operator adds**, e.g. `192.168.30.0/24` for an IoT
     VLAN, or `10.0.20.10-10.0.20.80`. Private addresses only; each range at
     most /22 (1,024 addresses).
3. **Host name** typed by the operator (`Meshtastic-3f2a`, `node.lan`), for
   people who prefer it.

A port that answers isn't proof: lots of things listen on 5000. Every hit is
**confirmed by speaking the protocol** before it is shown:

- Meshtastic: framed `want_config_id`; accept only a `0x94 0xC3` framed
  `FromRadio` carrying `my_info`; read node number, long/short name, firmware,
  hardware; send `disconnect`; close.
- MeshCore: `<`-framed `APP_START`; accept only a `>`-framed `SELF_INFO`;
  read public key and node name; send `DEVICE_QUERY` for firmware/model;
  close.

Results list: network badge, node name, address, identity (node id or key
prefix), firmware/model, and **Add** or **Already added**.

## Pairing: Zeus remembers the node, not the address

When a node is added, Zeus stores its **identity** (Meshtastic node number /
MeshCore public key) alongside the last address. On every connect it checks
the identity:

- **Same node** → connect as normal.
- **Different node at that address** (router reused the IP, board swapped) →
  refuse, show "a different node is at this address", and start looking for
  the right one. Conversations stay bound to the paired node, so replies can
  never go out from the wrong station.
- **Re-pair** is an explicit operator action (e.g. after re-flashing, which
  can change a MeshCore key).

## Finding it again when its address changes

Option **Find this node automatically if its address changes** — on by
default. When the paired node stops answering at its last address:

1. Re-resolve the host name, if one was given.
2. Meshtastic: look for its node id in mDNS.
3. Scan only the /24 it was last seen in, plus the operator's extra ranges,
   on that node's port only, confirming identity.
4. Found → update the stored address, connect, note it in the node's status
   ("moved from .47 to .52").
5. Not found → retry with backoff (1, 2, 5, then every 10 minutes), never in
   a tight loop; show "searching" with the last attempt time.

This is LAN traffic only; nothing is transmitted on the mesh.

## Etiquette and limits

- **Scans run only when the operator presses Scan**, during first setup, or
  for the automatic re-find above. No background sweeps of the network.
- Bounded: ports 4403 and 5000 only; at most 64 probes at once; 300 ms
  connect timeout; identify step 3 s; a /24 finishes in a few seconds.
- **MeshCore side effect:** probing a MeshCore node disconnects any app
  using it at that moment. The scan skips nodes Zeus is already connected to
  and says so up front: "Scanning may briefly disconnect a phone or
  meshcore-cli connected to another MeshCore node over Wi-Fi."
- Private address ranges only (10/8, 172.16/12, 192.168/16, and IPv6 ULA /
  link-local for mDNS). Public addresses are refused, here and in manual
  configuration.
- **VLANs:** the router/firewall must allow the Zeus PC to open TCP 4403 /
  5000 to the nodes' VLAN. Zeus can't fix a firewall; when a configured range
  answers nothing at all, the help explains this.

## Zeus review notes

- Uses `System.Net.Sockets` (TCP, UDP multicast for mDNS) and
  `System.Net.NetworkInformation` (to read the PC's own subnets): covered by
  the declared `NetworkAccess` capability. No raw sockets, ARP, ICMP or
  external processes.
- No third-party mDNS library (repo rule: no non-Microsoft packages); a
  minimal DNS-SD query/response parser is written in the shared library and
  fuzz-tested like the protocol parsers.
- The PR's capability section must describe the scan exactly as above:
  operator-triggered, private ranges, two ports, identity-confirmed.

## Later: gateway

With the Pi gateway, nodes are on USB and none of this is needed for them;
Zeus finds the **gateway** with the same mDNS + scan approach
(`_mesh-gateway._tcp`, its own port).
