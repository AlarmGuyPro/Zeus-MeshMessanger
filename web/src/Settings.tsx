// SPDX-License-Identifier: GPL-3.0-or-later
import { useEffect, useState } from "react";
import { errorText, networkLabel, type Client, type FoundDto, type Network, type NodeDto, type StatusDto } from "./api";
import { NetworkBadge, usePoll } from "./Panel";
import { cx } from "./styles";

function TxNo() {
  return <span className={cx("tx-label")}>Transmits: no</span>;
}

export function Settings({ client, status, onChanged }: { client: Client; status: StatusDto; onChanged: () => void }) {
  return (
    <div className={cx("pane")}>
      <AddNode client={client} status={status} onChanged={onChanged} />
      {status.nodes.map((n) => (
        <NodeEditor key={n.id} client={client} node={n} onChanged={onChanged} />
      ))}
      <ScanRanges client={client} status={status} onChanged={onChanged} />
      <History client={client} status={status} onChanged={onChanged} />
    </div>
  );
}

// ------------------------------------------------------------------ add node / scan

function AddNode({ client, status, onChanged }: { client: Client; status: StatusDto; onChanged: () => void }) {
  const [poll, setPoll] = useState(2000);
  const [discovery, refresh] = usePoll(() => client.discovery(), poll, [client]);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState<string | null>(null);
  const [manual, setManual] = useState(false);

  const scan = discovery.kind === "ready" ? discovery.data.scan : null;
  const running = !!scan?.running;
  useEffect(() => setPoll(running ? 700 : 4000), [running]);

  const start = async () => {
    setError(null);
    try {
      await client.startScan([], true);
      refresh();
    } catch (err) {
      setError(errorText(err));
    }
  };

  const add = async (f: FoundDto) => {
    setAdding(f.identity);
    setError(null);
    try {
      await client.addNode({ network: f.network, host: f.host, port: f.port, identity: f.identity });
      onChanged();
      refresh();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setAdding(null);
    }
  };

  const networks = discovery.kind === "ready" ? discovery.data.defaultNetworks : [];
  const pct = scan && scan.total > 0 ? Math.round((100 * scan.probed) / scan.total) : 0;

  return (
    <section className={cx("card")} aria-label="Add node">
      <div className={cx("card-head")}>
        <h2 className={cx("h")}>Add node</h2>
        <span className={cx("grow")} />
        <TxNo />
        {running ? (
          <button type="button" className={cx("button")} onClick={() => void client.stopScan().then(refresh)}>
            Stop
          </button>
        ) : (
          <button type="button" className={`${cx("button")} ${cx("button--primary")}`} onClick={() => void start()}>
            Scan
          </button>
        )}
      </div>
      <p className={cx("help")}>
        Scan looks for Meshtastic and MeshCore nodes on {networks.length ? networks.join(", ") : "your network"}
        {status.scanRanges.length ? " (including your extra networks)" : ""} and checks each one really is a mesh node. It
        stays on your network — nothing is sent on the mesh. It can briefly disconnect a phone or meshcore-cli using a{" "}
        <em>different</em> MeshCore node over Wi-Fi.
      </p>
      {running && scan && (
        <div role="status">
          <div className={cx("progress")} aria-hidden="true">
            <div className={cx("progress-bar")} style={{ width: `${pct}%` }} />
          </div>
          <span className={cx("muted")}>
            {scan.phase === "mdns" ? "Listening for Meshtastic announcements…" : `Checked ${scan.probed} of ${scan.total}`}
          </span>
        </div>
      )}
      {scan && !running && scan.phase !== "idle" && (
        <span className={cx("muted")}>
          {scan.cancelled ? "Scan stopped." : scan.error ? `Scan failed: ${scan.error}` : `Scan finished: ${scan.found.length} node(s) found.`}
        </span>
      )}
      {scan?.quietNetworks.length ? (
        <p className={`${cx("notice")} ${cx("notice--warn")}`} role="status">
          Nothing answered on {scan.quietNetworks.join(", ")}. If nodes live there, your router or firewall must let this
          computer reach TCP 4403 (Meshtastic) and 5000 (MeshCore) on that network.
        </p>
      ) : null}
      {scan && scan.found.length > 0 && (
        <div className={cx("scroll-x")}>
          <table className={cx("table")}>
            <thead>
              <tr>
                <th>Node</th>
                <th>Address</th>
                <th>Identity</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {scan.found.map((f) => (
                <tr key={`${f.network}-${f.identity}`}>
                  <td>
                    <NetworkBadge network={f.network} /> <strong>{f.name}</strong>
                    {f.shortName ? <span className={cx("muted")}> ({f.shortName})</span> : null}
                    <div className={cx("muted")} style={{ fontSize: 11 }}>
                      {[f.model, f.firmware, f.foundBy.includes("mdns") ? "announced on this network" : "found by scan"]
                        .filter(Boolean)
                        .join(" · ")}
                    </div>
                  </td>
                  <td className={cx("mono")}>{f.host}</td>
                  <td className={cx("mono")}>{f.identity.length > 14 ? `${f.identity.slice(0, 10)}…` : f.identity}</td>
                  <td>
                    {f.addedAs ? (
                      <span className={cx("ok")}>✓ Added</span>
                    ) : (
                      <button
                        type="button"
                        className={`${cx("button")} ${cx("button--primary")}`}
                        disabled={adding !== null}
                        onClick={() => void add(f)}
                      >
                        {adding === f.identity ? "Adding…" : "Add"}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {error && (
        <p className={cx("over")} role="alert">
          {error}
        </p>
      )}
      <div>
        <button type="button" className={cx("button")} aria-expanded={manual} onClick={() => setManual(!manual)}>
          {manual ? "Hide manual entry" : "Add manually"}
        </button>
      </div>
      {manual && <ManualAdd client={client} onAdded={onChanged} />}
    </section>
  );
}

function ManualAdd({ client, onAdded }: { client: Client; onAdded: () => void }) {
  const [network, setNetwork] = useState<Network | "">("");
  const [host, setHost] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (!network) return;
    setBusy(true);
    setError(null);
    try {
      await client.addNode({ network, host, name });
      setHost("");
      setName("");
      onAdded();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={cx("row")} style={{ alignItems: "flex-end" }}>
      <label className={cx("field")}>
        <span>Network</span>
        <select className={cx("input")} value={network} onChange={(e) => setNetwork(e.target.value as Network)}>
          <option value="" disabled>
            Choose…
          </option>
          <option value="meshtastic">Meshtastic (port 4403)</option>
          <option value="meshcore">MeshCore (port 5000)</option>
        </select>
      </label>
      <label className={`${cx("field")} ${cx("grow")}`}>
        <span>Address or host name (host:port for a non-standard port)</span>
        <input className={cx("input")} value={host} onChange={(e) => setHost(e.target.value)} placeholder="192.168.1.47" />
      </label>
      <label className={cx("field")}>
        <span>Name in Zeus (optional)</span>
        <input className={cx("input")} value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <button
        type="button"
        className={`${cx("button")} ${cx("button--primary")}`}
        disabled={!network || !host.trim() || busy}
        onClick={() => void submit()}
      >
        {busy ? "Adding…" : "Add"}
      </button>
      {error && (
        <p className={cx("over")} role="alert" style={{ flexBasis: "100%" }}>
          {error}
        </p>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ node editor

function NodeEditor({ client, node, onChanged }: { client: Client; node: NodeDto; onChanged: () => void }) {
  const [name, setName] = useState(node.name);
  const [host, setHost] = useState(node.host);
  const [port, setPort] = useState(String(node.port));
  const [error, setError] = useState<string | null>(null);
  const [confirmRemove, setConfirmRemove] = useState(false);

  useEffect(() => setHost(node.host), [node.host]);

  const run = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
      onChanged();
    } catch (err) {
      setError(errorText(err));
    }
  };

  const dirty = name !== node.name || host !== node.host || port !== String(node.port);

  return (
    <section className={cx("card")} aria-label={`Node ${node.name}`}>
      <div className={cx("card-head")}>
        <NetworkBadge network={node.network} />
        <h2 className={cx("h")}>{node.name}</h2>
        <span className={node.state === "connected" ? cx("ok") : node.state === "error" ? cx("warn") : cx("muted")}>
          {node.state}
        </span>
        <span className={cx("muted")}>{node.detail}</span>
      </div>
      <div className={cx("row")} style={{ alignItems: "flex-end" }}>
        <label className={`${cx("field")} ${cx("grow")}`}>
          <span>Name shown in Zeus</span>
          <input className={cx("input")} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label className={`${cx("field")} ${cx("grow")}`}>
          <span>Address (found by scan; you can type one)</span>
          <input className={`${cx("input")} ${cx("mono")}`} value={host} onChange={(e) => setHost(e.target.value)} />
        </label>
        <label className={cx("field")} style={{ width: 90 }}>
          <span>Port</span>
          <input className={`${cx("input")} ${cx("mono")}`} inputMode="numeric" value={port} onChange={(e) => setPort(e.target.value)} />
        </label>
        <button
          type="button"
          className={`${cx("button")} ${cx("button--primary")}`}
          disabled={!dirty}
          onClick={() => void run(() => client.updateNode(node.id, { name, host, port: Number(port) || 0 }))}
        >
          Save
        </button>
      </div>
      <p className={cx("help")}>
        {networkLabel[node.network]} · paired with{" "}
        <span className={cx("mono")}>{node.identity ? (node.identity.length > 14 ? `${node.identity.slice(0, 10)}…` : node.identity) : "the first node found at this address"}</span>
        . Zeus refuses a different node at this address, so replies can never go out from the wrong station.
      </p>
      <label className={cx("row")}>
        <input
          type="checkbox"
          checked={node.enabled}
          onChange={(e) => void run(() => client.updateNode(node.id, { enabled: e.target.checked }))}
        />
        <span>Enabled</span>
        <TxNo />
      </label>
      <label className={cx("row")}>
        <input
          type="checkbox"
          checked={node.findIfAddressChanges}
          onChange={(e) => void run(() => client.updateNode(node.id, { findIfAddressChanges: e.target.checked }))}
        />
        <span>Find this node automatically if its address changes</span>
        <span className={cx("tx-label")}>Transmits: no (network only)</span>
      </label>
      <p className={cx("help")}>
        If it stops answering, Zeus checks its host name, then its network announcement (Meshtastic), then scans the network it
        was last on and your extra networks, and reconnects when it finds the same node.
      </p>
      <div className={cx("row")}>
        <button type="button" className={cx("button")} onClick={() => void run(() => client.repairNode(node.id))}>
          Re-pair
        </button>
        <span className={cx("help")}>After re-flashing or replacing the board.</span>
        <span className={cx("grow")} />
        {confirmRemove ? (
          <>
            <span className={cx("warn")}>Remove {node.name}? Its conversations stay in history.</span>
            <button type="button" className={cx("button")} onClick={() => void run(() => client.removeNode(node.id))}>
              Remove
            </button>
            <button type="button" className={cx("button")} onClick={() => setConfirmRemove(false)}>
              Keep
            </button>
          </>
        ) : (
          <button type="button" className={cx("button")} onClick={() => setConfirmRemove(true)}>
            Remove node…
          </button>
        )}
      </div>
      {error && (
        <p className={cx("over")} role="alert">
          {error}
        </p>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ ranges & history

function ScanRanges({ client, status, onChanged }: { client: Client; status: StatusDto; onChanged: () => void }) {
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);

  const save = async (ranges: string[]) => {
    setError(null);
    try {
      await client.saveSettings({ scanRanges: ranges });
      setDraft("");
      onChanged();
    } catch (err) {
      setError(errorText(err));
    }
  };

  return (
    <section className={cx("card")} aria-label="Extra networks to scan">
      <div className={cx("card-head")}>
        <h2 className={cx("h")}>Extra networks to scan</h2>
        <span className={cx("grow")} />
        <TxNo />
      </div>
      <p className={cx("help")}>
        Other VLANs to search, e.g. an IoT network. Used by Scan and when looking for a node that moved. Local addresses only, up
        to 4,094 addresses each (a /20).
      </p>
      <div className={cx("row")}>
        {status.scanRanges.length === 0 && <span className={cx("muted")}>None.</span>}
        {status.scanRanges.map((r) => (
          <span key={r} className={cx("chip")}>
            <span className={cx("mono")}>{r}</span>
            <button
              type="button"
              className={cx("button")}
              aria-label={`Remove ${r}`}
              onClick={() => void save(status.scanRanges.filter((x) => x !== r))}
            >
              Remove
            </button>
          </span>
        ))}
      </div>
      <div className={cx("row")}>
        <label className={`${cx("field")} ${cx("grow")}`}>
          <span>Add a network</span>
          <input className={`${cx("input")} ${cx("mono")}`} placeholder="192.168.30.0/24" value={draft} onChange={(e) => setDraft(e.target.value)} />
        </label>
        <button
          type="button"
          className={cx("button")}
          style={{ alignSelf: "flex-end" }}
          disabled={!draft.trim()}
          onClick={() => void save([...status.scanRanges, draft.trim()])}
        >
          Add
        </button>
      </div>
      {error && (
        <p className={cx("over")} role="alert">
          {error}
        </p>
      )}
    </section>
  );
}

function History({ client, status, onChanged }: { client: Client; status: StatusDto; onChanged: () => void }) {
  const [value, setValue] = useState(String(status.historyPerConversation));
  return (
    <section className={cx("card")} aria-label="History">
      <div className={cx("card-head")}>
        <h2 className={cx("h")}>History</h2>
        <span className={cx("grow")} />
        <TxNo />
      </div>
      <div className={cx("row")}>
        <label className={cx("field")}>
          <span>Messages kept per conversation (20–5000)</span>
          <input className={`${cx("input")} ${cx("mono")}`} inputMode="numeric" value={value} onChange={(e) => setValue(e.target.value)} />
        </label>
        <button
          type="button"
          className={cx("button")}
          style={{ alignSelf: "flex-end" }}
          disabled={value === String(status.historyPerConversation)}
          onClick={() => void client.saveSettings({ historyPerConversation: Number(value) || 500 }).then(onChanged)}
        >
          Save
        </button>
      </div>
      <p className={cx("help")}>Kept across Zeus restarts. Older messages are removed from Zeus only.</p>
    </section>
  );
}
